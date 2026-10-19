using System.Text.Json.Nodes;
using Diary.Agent.Configuration;

namespace Diary.AgentTests;

[TestClass]
public sealed class AiConnectionSharePackageServiceTests
{
    [TestMethod]
    public void ExportImport_StripsCredentialReferencesAndRebasesImportedProfile()
    {
        var service = new AiConnectionSharePackageService();
        var profile = CreateProfile() with
        {
            Authentication = new AiAuthenticationConfiguration
            {
                Kind = AiAuthenticationKind.Header,
                CredentialReference = "secret-api-reference",
                HeaderName = "X-Api-Key",
                HeaderPrefix = "Token ",
            },
            Proxy = new AiProxyConfiguration
            {
                Mode = AiProxyMode.Custom,
                Address = new Uri("http://proxy.example:8080"),
                BypassList = ["localhost", ".internal.example"],
                UsernameCredentialReference = "secret-proxy-user-reference",
                PasswordCredentialReference = "secret-proxy-password-reference",
            },
            Compatibility = new AiCompatibilityOptions
            {
                SendToolChoice = false,
                SendStreamOptions = false,
                AnthropicVersion = "2024-01-01",
            },
            Headers =
            [
                new AiRequestHeader { Name = "X-Tenant", Value = "engineering" },
                new AiRequestHeader
                {
                    Name = "X-Internal-Token",
                    CredentialReference = "secret-header-reference",
                    Prefix = "Bearer ",
                },
            ],
        };

        var exported = service.Export([profile]);

        Assert.AreEqual(1, exported.ProfileCount);
        Assert.AreEqual(4, exported.StrippedCredentialCount);
        Assert.IsFalse(exported.Content.Contains("secret-api-reference", StringComparison.Ordinal));
        Assert.IsFalse(exported.Content.Contains("secret-proxy-user-reference", StringComparison.Ordinal));
        Assert.IsFalse(exported.Content.Contains("secret-proxy-password-reference", StringComparison.Ordinal));
        Assert.IsFalse(exported.Content.Contains("secret-header-reference", StringComparison.Ordinal));
        Assert.IsFalse(exported.Content.Contains("credentialReference", StringComparison.OrdinalIgnoreCase));

        var imported = service.Import(exported.Content, [profile.Id]);

        Assert.AreEqual(1, imported.Profiles.Count);
        Assert.AreEqual(1, imported.RenamedProfileCount);
        Assert.AreEqual(4, imported.RequiredCredentialCount);
        var result = imported.Profiles.Single();
        Assert.AreEqual("internal-anthropic-imported", result.Id);
        Assert.AreEqual(profile.DisplayName, result.DisplayName);
        Assert.AreEqual(profile.Protocol, result.Protocol);
        Assert.AreEqual(profile.BaseUri, result.BaseUri);
        Assert.AreEqual(profile.Model, result.Model);
        Assert.AreEqual(profile.ContextWindowTokens, result.ContextWindowTokens);
        Assert.AreEqual("diary.ai/internal-anthropic-imported/api-key", result.Authentication.CredentialReference);
        Assert.AreEqual("diary.ai/internal-anthropic-imported/proxy-user", result.Proxy.UsernameCredentialReference);
        Assert.AreEqual("diary.ai/internal-anthropic-imported/proxy-password", result.Proxy.PasswordCredentialReference);
        Assert.AreEqual("engineering", result.Headers[0].Value);
        Assert.AreEqual("diary.ai/internal-anthropic-imported/header-2", result.Headers[1].CredentialReference);
        Assert.IsFalse(result.Compatibility.SendToolChoice);
        Assert.IsFalse(result.Compatibility.SendStreamOptions);
        Assert.AreEqual("2024-01-01", result.Compatibility.AnthropicVersion);
        Assert.IsEmpty(AiConnectionProfileValidator.Validate(result));
    }

    [TestMethod]
    [DataRow(AiProtocol.OpenAiChatCompletions)]
    [DataRow(AiProtocol.OpenAiResponses)]
    [DataRow(AiProtocol.AnthropicMessages)]
    public void ExportImport_RoundTripsSupportedProtocol(AiProtocol protocol)
    {
        var service = new AiConnectionSharePackageService();
        var source = CreateProfile() with { Protocol = protocol };

        var result = service.Import(service.Export([source]).Content, []);

        Assert.AreEqual(protocol, result.Profiles.Single().Protocol);
        Assert.AreEqual(source.Id, result.Profiles.Single().Id);
    }

    [TestMethod]
    public void Import_RejectsUnsupportedVersionAndDuplicateIds()
    {
        var service = new AiConnectionSharePackageService();
        var document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        document["version"] = 99;
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(document.ToJsonString(), []));

        document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        var profiles = document["profiles"]!.AsArray();
        profiles.Add(profiles[0]!.DeepClone());
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => service.Import(document.ToJsonString(), []));
        StringAssert.Contains(exception.Message, "重复 ID");
    }

    [TestMethod]
    public void Import_RejectsUnknownMembersAndOversizedPackage()
    {
        var service = new AiConnectionSharePackageService();
        var document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        document["unexpected"] = true;
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(document.ToJsonString(), []));

        document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        document.Remove("format");
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(document.ToJsonString(), []));

        document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        document["profiles"]![0]!["compatibility"]!.AsObject().Remove("sendToolChoice");
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(document.ToJsonString(), []));

        var oversized = new string('x', AiConnectionSharePackageService.MaximumPackageBytes + 1);
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(oversized, []));
    }

    [TestMethod]
    public void Import_RejectsNullNestedConfigurationAndNumericEnums()
    {
        var service = new AiConnectionSharePackageService();
        var document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        document["profiles"]![0]!["authentication"] = null;
        var missingAuthentication = Assert.ThrowsExactly<InvalidDataException>(
            () => service.Import(document.ToJsonString(), []));
        StringAssert.Contains(missingAuthentication.Message, "缺少认证");

        document = JsonNode.Parse(service.Export([CreateProfile()]).Content)!.AsObject();
        document["profiles"]![0]!["protocol"] = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => service.Import(document.ToJsonString(), []));
    }

    private static AiConnectionProfile CreateProfile() => new()
    {
        Id = "internal-anthropic",
        DisplayName = "内部 Anthropic 兼容服务",
        Protocol = AiProtocol.AnthropicMessages,
        BaseUri = new Uri("https://ai.internal.example/"),
        RequestPathOverride = "v1/messages",
        Model = "deepseek-v4-pro",
        ContextWindowTokens = 1_000_000,
        AutomaticContextCompression = true,
        ContextCompressionThresholdPercent = 80,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        RequestTimeout = TimeSpan.FromMinutes(5),
        CheckCertificateRevocation = false,
        Authentication = new AiAuthenticationConfiguration
        {
            Kind = AiAuthenticationKind.Bearer,
            CredentialReference = "diary.ai/internal-anthropic/api-key",
        },
        Proxy = new AiProxyConfiguration { Mode = AiProxyMode.System },
    };
}
