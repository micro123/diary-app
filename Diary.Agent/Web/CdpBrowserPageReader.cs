using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Web;

public sealed class CdpBrowserPageReader(
    WebAccessPolicy policy,
    WebTargetValidator validator,
    IAiCredentialStore credentials,
    SystemBrowserLocator browserLocator,
    string profileRootDirectory) : IBrowserPageReader
{
    private static readonly HashSet<string> BlockedResourceTypes = new(StringComparer.Ordinal)
    {
        "Font",
        "Image",
        "Manifest",
        "Media",
        "Ping",
        "WebSocket",
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<BrowserPageReadResult> ReadAsync(
        BrowserPageReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (policy.Browser.Mode == BrowserAccessMode.Disabled)
            throw new BrowserPageReadException("browser_disabled", "浏览器网页读取未启用。");
        await validator.ValidateAsync(request.Url, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(policy.Timeout);
            return await ReadCoreAsync(request, timeout.Token);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new BrowserPageReadException("cancelled", "浏览器网页读取已取消。", exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new BrowserPageReadException("timeout", "浏览器网页读取超时。", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<BrowserPageReadResult> ReadCoreAsync(
        BrowserPageReadRequest request,
        CancellationToken cancellationToken)
    {
        await using var browser = await OpenBrowserAsync(cancellationToken);
        await using var connection = new CdpConnection();
        await connection.ConnectAsync(browser.WebSocketEndpoint, cancellationToken);
        string? targetId = null;
        try
        {
            try
            {
                await connection.SendAsync(
                    "Browser.setDownloadBehavior",
                    new JsonObject { ["behavior"] = "deny" },
                    cancellationToken: cancellationToken);
            }
            catch (BrowserPageReadException)
            {
                // 旧版 Chromium 可能不支持 Browser 级下载策略；工具本身不会触发点击或提交。
            }

            var target = await connection.SendAsync(
                "Target.createTarget",
                new JsonObject { ["url"] = "about:blank" },
                cancellationToken: cancellationToken);
            targetId = target.GetProperty("targetId").GetString()
                ?? throw new BrowserPageReadException("cdp_error", "浏览器未返回页面 Target ID。");
            var attached = await connection.SendAsync(
                "Target.attachToTarget",
                new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
                cancellationToken: cancellationToken);
            var sessionId = attached.GetProperty("sessionId").GetString()
                ?? throw new BrowserPageReadException("cdp_error", "浏览器未返回页面 Session ID。");

            var domReady = NewCompletion();
            var loaded = NewCompletion();
            var fatal = new TaskCompletionSource<BrowserPageReadException>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.EventReceived += cdpEvent => HandleEventAsync(
                connection,
                sessionId,
                request.Url,
                cdpEvent,
                domReady,
                loaded,
                fatal,
                cancellationToken);

            await connection.SendAsync("Page.enable", sessionId: sessionId, cancellationToken: cancellationToken);
            await connection.SendAsync("Runtime.enable", sessionId: sessionId, cancellationToken: cancellationToken);
            await connection.SendAsync("Network.enable", sessionId: sessionId, cancellationToken: cancellationToken);
            await connection.SendAsync(
                "Network.setBypassServiceWorker",
                new JsonObject { ["bypass"] = true },
                sessionId,
                cancellationToken);
            await connection.SendAsync(
                "Fetch.enable",
                new JsonObject
                {
                    ["handleAuthRequests"] = true,
                    ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*" }),
                },
                sessionId,
                cancellationToken);

            var navigation = await connection.SendAsync(
                "Page.navigate",
                new JsonObject { ["url"] = request.Url.AbsoluteUri },
                sessionId,
                cancellationToken);
            if (navigation.TryGetProperty("errorText", out var errorText)
                && !string.IsNullOrWhiteSpace(errorText.GetString()))
            {
                throw new BrowserPageReadException("navigation_failed", $"网页导航失败：{errorText.GetString()}");
            }

            var readiness = request.WaitMode == BrowserWaitMode.Load ? loaded.Task : domReady.Task;
            var completed = await Task.WhenAny(readiness, fatal.Task).WaitAsync(cancellationToken);
            if (completed == fatal.Task)
                throw await fatal.Task;
            await readiness;
            if (request.WaitMode == BrowserWaitMode.ShortDelay || policy.Browser.RenderDelayMilliseconds > 0)
                await Task.Delay(policy.Browser.RenderDelayMilliseconds, cancellationToken);

            var extracted = await ExtractAsync(connection, sessionId, cancellationToken);
            var maximum = Math.Min(
                request.MaxCharacters ?? policy.MaxExtractedCharacters,
                policy.MaxExtractedCharacters);
            maximum = Math.Max(1_000, maximum);
            var text = NormalizeWhitespace(extracted.Content);
            var truncated = text.Length > maximum;
            if (truncated)
                text = text[..maximum];
            return new BrowserPageReadResult(
                request.Url,
                extracted.Url,
                extracted.Title,
                DateTimeOffset.UtcNow,
                text,
                extracted.Links,
                truncated,
                browser.DisplayName);
        }
        catch (WebFetchException exception)
        {
            throw new BrowserPageReadException(
                $"web_{exception.Code.ToString().ToLowerInvariant()}",
                exception.Message,
                exception);
        }
        finally
        {
            if (targetId is not null)
            {
                try
                {
                    await connection.SendAsync(
                        "Target.closeTarget",
                        new JsonObject { ["targetId"] = targetId },
                        cancellationToken: CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private async ValueTask HandleEventAsync(
        CdpConnection connection,
        string sessionId,
        Uri authenticationOrigin,
        CdpEvent cdpEvent,
        TaskCompletionSource domReady,
        TaskCompletionSource loaded,
        TaskCompletionSource<BrowserPageReadException> fatal,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(cdpEvent.SessionId, sessionId, StringComparison.Ordinal))
            return;
        switch (cdpEvent.Method)
        {
            case "Page.domContentEventFired":
                domReady.TrySetResult();
                return;
            case "Page.loadEventFired":
                loaded.TrySetResult();
                return;
            case "Fetch.requestPaused":
                await ContinueOrBlockRequestAsync(
                    connection,
                    sessionId,
                    authenticationOrigin,
                    cdpEvent.Parameters,
                    fatal,
                    cancellationToken);
                return;
            case "Fetch.authRequired":
                await ContinueAuthenticationAsync(connection, sessionId, cdpEvent.Parameters, cancellationToken);
                return;
        }
    }

    private async ValueTask ContinueOrBlockRequestAsync(
        CdpConnection connection,
        string sessionId,
        Uri authenticationOrigin,
        JsonElement parameters,
        TaskCompletionSource<BrowserPageReadException> fatal,
        CancellationToken cancellationToken)
    {
        var requestId = parameters.GetProperty("requestId").GetString()!;
        var request = parameters.GetProperty("request");
        var resourceType = parameters.TryGetProperty("resourceType", out var typeElement)
            ? typeElement.GetString() ?? string.Empty
            : string.Empty;
        if (!Uri.TryCreate(request.GetProperty("url").GetString(), UriKind.Absolute, out var uri))
        {
            await FailRequestAsync(connection, sessionId, requestId, cancellationToken);
            if (string.Equals(resourceType, "Document", StringComparison.Ordinal))
            {
                fatal.TrySetResult(new BrowserPageReadException(
                    "invalid_url",
                    "浏览器主文档请求包含无效 URL。"));
            }
            return;
        }
        if (BlockedResourceTypes.Contains(resourceType))
        {
            await FailRequestAsync(connection, sessionId, requestId, cancellationToken);
            return;
        }
        try
        {
            var target = await validator.ValidateAsync(uri, cancellationToken);
            var command = new JsonObject { ["requestId"] = requestId };
            if (WebTargetValidator.IsSameOrigin(authenticationOrigin, uri)
                && target.InternalSite is { AuthenticationHeaderName: not null } site)
            {
                var credential = string.IsNullOrWhiteSpace(site.CredentialReference)
                    ? null
                    : await credentials.GetAsync(site.CredentialReference, cancellationToken);
                if (credential is null)
                    throw new BrowserPageReadException("authentication_unavailable", "内部网页认证凭据不可用。");
                var headers = new JsonArray();
                if (request.TryGetProperty("headers", out var requestHeaders))
                {
                    foreach (var header in requestHeaders.EnumerateObject())
                    {
                        if (!string.Equals(header.Name, site.AuthenticationHeaderName, StringComparison.OrdinalIgnoreCase))
                        {
                            headers.Add(new JsonObject
                            {
                                ["name"] = header.Name,
                                ["value"] = header.Value.GetString() ?? string.Empty,
                            });
                        }
                    }
                }
                headers.Add(new JsonObject
                {
                    ["name"] = site.AuthenticationHeaderName,
                    ["value"] = (site.AuthenticationHeaderPrefix ?? string.Empty) + credential.Reveal(),
                });
                command["headers"] = headers;
            }
            await connection.SendAsync("Fetch.continueRequest", command, sessionId, cancellationToken);
        }
        catch (Exception exception) when (exception is WebFetchException or BrowserPageReadException)
        {
            await FailRequestAsync(connection, sessionId, requestId, cancellationToken);
            if (string.Equals(resourceType, "Document", StringComparison.Ordinal))
            {
                fatal.TrySetResult(exception as BrowserPageReadException
                                   ?? new BrowserPageReadException(
                                       "target_blocked",
                                       exception.Message,
                                       exception));
            }
        }
    }

    private async ValueTask ContinueAuthenticationAsync(
        CdpConnection connection,
        string sessionId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var response = new JsonObject { ["response"] = "Default" };
        if (parameters.TryGetProperty("authChallenge", out var challenge)
            && challenge.TryGetProperty("source", out var source)
            && string.Equals(source.GetString(), "Proxy", StringComparison.OrdinalIgnoreCase)
            && policy.Proxy.Mode == AiProxyMode.Custom)
        {
            var username = await ReadCredentialAsync(policy.Proxy.UsernameCredentialReference, cancellationToken);
            var password = await ReadCredentialAsync(policy.Proxy.PasswordCredentialReference, cancellationToken);
            response = username is null && password is null
                ? new JsonObject { ["response"] = "CancelAuth" }
                : new JsonObject
                {
                    ["response"] = "ProvideCredentials",
                    ["username"] = username ?? string.Empty,
                    ["password"] = password ?? string.Empty,
                };
        }
        await connection.SendAsync(
            "Fetch.continueWithAuth",
            new JsonObject
            {
                ["requestId"] = parameters.GetProperty("requestId").GetString(),
                ["authChallengeResponse"] = response,
            },
            sessionId,
            cancellationToken);
    }

    private async ValueTask<ExtractedPage> ExtractAsync(
        CdpConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var maxLinks = policy.Browser.MaxLinks.ToString(CultureInfo.InvariantCulture);
        var expression = $$"""
            (() => {
              const root = document.body ? document.body.cloneNode(true) : null;
              if (root) {
                root.querySelectorAll("script,style,form,noscript,template,svg,canvas,iframe,object,embed,[hidden],[aria-hidden='true']")
                  .forEach(node => node.remove());
              }
              const content = root ? (root.textContent || "") : "";
              const links = Array.from(document.querySelectorAll("a[href]"))
                .slice(0, {{maxLinks}})
                .map(link => ({ text: (link.innerText || link.textContent || "").trim(), url: link.href }))
                .filter(link => link.url.startsWith("http://") || link.url.startsWith("https://"));
              return JSON.stringify({ url: location.href, title: document.title || null, content, links });
            })()
            """;
        var evaluation = await connection.SendAsync(
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
            },
            sessionId,
            cancellationToken);
        if (evaluation.TryGetProperty("exceptionDetails", out var exceptionDetails))
            throw new BrowserPageReadException("extraction_failed", $"动态网页正文提取失败：{exceptionDetails.GetRawText()}");
        var value = evaluation.GetProperty("result").GetProperty("value").GetString()
            ?? throw new BrowserPageReadException("extraction_failed", "动态网页正文提取没有返回结果。");
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        var finalUrl = new Uri(root.GetProperty("url").GetString()!, UriKind.Absolute);
        await validator.ValidateAsync(finalUrl, cancellationToken);
        var links = root.GetProperty("links").EnumerateArray()
            .Select(item => new BrowserPageLink(
                item.GetProperty("text").GetString() ?? string.Empty,
                new Uri(item.GetProperty("url").GetString()!, UriKind.Absolute)))
            .ToArray();
        return new ExtractedPage(
            finalUrl,
            root.TryGetProperty("title", out var title) && title.ValueKind != JsonValueKind.Null
                ? title.GetString()
                : null,
            root.GetProperty("content").GetString() ?? string.Empty,
            links);
    }

    private async ValueTask<OpenedBrowser> OpenBrowserAsync(CancellationToken cancellationToken)
    {
        if (policy.Browser.Mode == BrowserAccessMode.Cdp)
        {
            var endpoint = policy.Browser.CdpEndpoint
                ?? throw new BrowserPageReadException("browser_unavailable", "没有配置 CDP Endpoint。");
            return new OpenedBrowser(
                await ResolveCdpWebSocketAsync(endpoint, cancellationToken),
                "CDP 浏览器",
                null,
                null,
                null);
        }
        var executable = browserLocator.Find(policy.Browser)
            ?? throw new BrowserPageReadException("browser_unavailable", "没有检测到可用的 Edge、Chrome 或 Chromium。");
        Directory.CreateDirectory(profileRootDirectory);
        var profileDirectory = Path.Combine(profileRootDirectory, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileDirectory);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = policy.Browser.Headless,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-component-update");
        startInfo.ArgumentList.Add("--mute-audio");
        if (policy.Browser.Headless)
            startInfo.ArgumentList.Add("--headless=new");
        ApplyProxyArguments(startInfo);
        startInfo.ArgumentList.Add("about:blank");
        var process = Process.Start(startInfo)
            ?? throw new BrowserPageReadException("browser_start_failed", "无法启动系统浏览器。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var endpoint = await WaitForDevToolsEndpointAsync(process, profileDirectory, cancellationToken);
            return new OpenedBrowser(
                endpoint,
                Path.GetFileNameWithoutExtension(executable),
                process,
                profileDirectory,
                TryDeleteProfile);
        }
        catch
        {
            TryStopProcess(process);
            TryDeleteProfile(profileDirectory);
            throw;
        }
    }

    private void ApplyProxyArguments(ProcessStartInfo startInfo)
    {
        switch (policy.Proxy.Mode)
        {
            case AiProxyMode.Direct:
                startInfo.ArgumentList.Add("--no-proxy-server");
                break;
            case AiProxyMode.Custom:
                startInfo.ArgumentList.Add($"--proxy-server={policy.Proxy.Address}");
                if (policy.Proxy.BypassList.Count > 0)
                    startInfo.ArgumentList.Add($"--proxy-bypass-list={string.Join(';', policy.Proxy.BypassList)}");
                break;
        }
    }

    private static async ValueTask<Uri> WaitForDevToolsEndpointAsync(
        Process process,
        string profileDirectory,
        CancellationToken cancellationToken)
    {
        var activePortPath = Path.Combine(profileDirectory, "DevToolsActivePort");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
                throw new BrowserPageReadException("browser_start_failed", "系统浏览器在建立 CDP 连接前退出。");
            if (File.Exists(activePortPath))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(activePortPath, cancellationToken);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                        return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
                }
                catch (IOException)
                {
                }
            }
            await Task.Delay(50, cancellationToken);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private static async ValueTask<Uri> ResolveCdpWebSocketAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (!endpoint.IsAbsoluteUri
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.Scheme is not ("http" or "https" or "ws" or "wss")
            || !IsLoopbackHost(endpoint))
        {
            throw new BrowserPageReadException("cdp_error", "CDP Endpoint 必须是本机 HTTP(S) 或 WebSocket 地址。");
        }
        if (endpoint.Scheme is "ws" or "wss")
            return endpoint;
        var baseUri = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + '/', UriKind.Absolute);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(new Uri(baseUri, "json/version"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var value = document.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var webSocket)
            || webSocket.Scheme is not ("ws" or "wss")
            || !IsLoopbackHost(webSocket))
        {
            throw new BrowserPageReadException("cdp_error", "CDP Endpoint 未返回有效的本机 WebSocket 地址。");
        }
        return webSocket;
    }

    private static async ValueTask FailRequestAsync(
        CdpConnection connection,
        string sessionId,
        string requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
                "Fetch.failRequest",
                new JsonObject { ["requestId"] = requestId, ["errorReason"] = "BlockedByClient" },
                sessionId,
                cancellationToken);
        }
        catch (BrowserPageReadException)
        {
        }
    }

    private async ValueTask<string?> ReadCredentialAsync(string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        return (await credentials.GetAsync(reference, cancellationToken))?.Reveal();
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsLoopbackHost(Uri uri) =>
        string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(uri.DnsSafeHost, out var address) && IPAddress.IsLoopback(address);

    private void TryDeleteProfile(string profileDirectory)
    {
        try
        {
            var root = Path.GetFullPath(profileRootDirectory) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(profileDirectory);
            if (target.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record ExtractedPage(
        Uri Url,
        string? Title,
        string Content,
        IReadOnlyList<BrowserPageLink> Links);

    private sealed class OpenedBrowser(
        Uri webSocketEndpoint,
        string displayName,
        Process? process,
        string? temporaryProfile,
        Action<string>? deleteProfile) : IAsyncDisposable
    {
        public Uri WebSocketEndpoint { get; } = webSocketEndpoint;

        public string DisplayName { get; } = displayName;

        public ValueTask DisposeAsync()
        {
            if (process is not null)
                TryStopProcess(process);
            if (temporaryProfile is not null)
                deleteProfile?.Invoke(temporaryProfile);
            return ValueTask.CompletedTask;
        }
    }
}
