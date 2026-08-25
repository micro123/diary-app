using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed record ScriptLastArgumentsScope(
    string ScriptId,
    ScriptEntryKind EntryKind,
    ScriptEditorTargetKind? EditorTargetKind = null);

public sealed record ScriptLastArgumentsEntry(
    string ScriptId,
    ScriptEntryKind EntryKind,
    ScriptEditorTargetKind? EditorTargetKind,
    string? SchemaFingerprint,
    IReadOnlyDictionary<string, string>? Arguments,
    string? LegacyArgumentsText,
    DateTimeOffset UpdatedAtUtc);

public interface IScriptLastArgumentsStore
{
    ValueTask<ScriptLastArgumentsEntry?> GetAsync(
        ScriptLastArgumentsScope scope,
        ScriptDescriptor descriptor,
        CancellationToken cancellationToken = default);

    ValueTask SaveV2Async(
        ScriptLastArgumentsScope scope,
        ScriptDescriptor descriptor,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default);

    ValueTask SaveV1Async(
        ScriptLastArgumentsScope scope,
        string argumentsText,
        CancellationToken cancellationToken = default);

    ValueTask ClearAsync(
        ScriptLastArgumentsScope scope,
        CancellationToken cancellationToken = default);

    ValueTask ClearScriptAsync(string scriptId, CancellationToken cancellationToken = default);

    ValueTask ClearAllAsync(CancellationToken cancellationToken = default);
}

[DiAutoRegister(singleton: true, serviceType: typeof(IScriptLastArgumentsStore))]
public sealed class ScriptLastArgumentsStore : IScriptLastArgumentsStore
{
    internal const int MaxEntryCount = 200;
    internal const int MaxFileBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _statePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ScriptLastArgumentsEntry>? _entries;

    public ScriptLastArgumentsStore(ILogger logger)
        : this(
            Path.Combine(
                FsTools.GetApplicationDataDirectory(),
                "ScriptState",
                "last-arguments.json"),
            logger)
    {
    }

    internal ScriptLastArgumentsStore(string statePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        _statePath = Path.GetFullPath(statePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<ScriptLastArgumentsEntry?> GetAsync(
        ScriptLastArgumentsScope scope,
        ScriptDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(descriptor);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (!_entries!.TryGetValue(GetKey(scope), out var entry))
                return null;
            if (descriptor.ApiVersion != ScriptApiVersion.V2 || entry.Arguments is null)
                return entry;

            var fingerprint = ScriptParameterSchemaFingerprint.Compute(descriptor);
            if (string.Equals(entry.SchemaFingerprint, fingerprint, StringComparison.Ordinal))
                return entry;

            var migrated = MigrateArguments(descriptor, entry.Arguments);
            return entry with
            {
                SchemaFingerprint = fingerprint,
                Arguments = migrated,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask SaveV2Async(
        ScriptLastArgumentsScope scope,
        ScriptDescriptor descriptor,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(arguments);
        var entry = new ScriptLastArgumentsEntry(
            scope.ScriptId,
            scope.EntryKind,
            scope.EditorTargetKind,
            ScriptParameterSchemaFingerprint.Compute(descriptor),
            arguments.ToImmutableDictionary(StringComparer.Ordinal),
            null,
            DateTimeOffset.UtcNow);
        return SaveAsync(scope, entry, cancellationToken);
    }

    public ValueTask SaveV1Async(
        ScriptLastArgumentsScope scope,
        string argumentsText,
        CancellationToken cancellationToken = default)
    {
        var entry = new ScriptLastArgumentsEntry(
            scope.ScriptId,
            scope.EntryKind,
            scope.EditorTargetKind,
            null,
            null,
            argumentsText ?? string.Empty,
            DateTimeOffset.UtcNow);
        return SaveAsync(scope, entry, cancellationToken);
    }

    public async ValueTask ClearAsync(
        ScriptLastArgumentsScope scope,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_entries!.Remove(GetKey(scope)))
                await PersistAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ClearScriptAsync(
        string scriptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            var keys = _entries!
                .Where(pair => string.Equals(pair.Value.ScriptId, scriptId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in keys)
                _entries!.Remove(key);
            if (keys.Length > 0)
                await PersistAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            if (_entries!.Count == 0)
                return;
            _entries.Clear();
            await PersistAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask SaveAsync(
        ScriptLastArgumentsScope scope,
        ScriptLastArgumentsEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _entries![GetKey(scope)] = entry;
            TrimToEntryLimit();
            await PersistAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
            return;
        _entries = new Dictionary<string, ScriptLastArgumentsEntry>(StringComparer.Ordinal);
        if (!File.Exists(_statePath))
            return;
        try
        {
            await using var stream = File.OpenRead(_statePath);
            var document = await JsonSerializer.DeserializeAsync<ScriptLastArgumentsDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            foreach (var entry in document?.Entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.ScriptId))
                    continue;
                _entries[GetKey(new ScriptLastArgumentsScope(
                    entry.ScriptId,
                    entry.EntryKind,
                    entry.EditorTargetKind))] = entry;
            }
            TrimToEntryLimit();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取脚本上次执行参数失败，已忽略损坏或不可读的状态文件：{Path}", _statePath);
            _entries.Clear();
        }
    }

    private async ValueTask PersistAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            while (true)
            {
                var document = new ScriptLastArgumentsDocument(
                    _entries!.Values.OrderByDescending(entry => entry.UpdatedAtUtc).ToArray());
                var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
                if (bytes.Length <= MaxFileBytes || _entries.Count == 0)
                {
                    await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
                    File.Move(temporaryPath, _statePath, true);
                    return;
                }

                var oldest = _entries.MinBy(pair => pair.Value.UpdatedAtUtc);
                _entries.Remove(oldest.Key);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void TrimToEntryLimit()
    {
        while (_entries!.Count > MaxEntryCount)
        {
            var oldest = _entries.MinBy(pair => pair.Value.UpdatedAtUtc);
            _entries.Remove(oldest.Key);
        }
    }

    private static ImmutableDictionary<string, string> MigrateArguments(
        ScriptDescriptor descriptor,
        IReadOnlyDictionary<string, string> arguments)
    {
        var migrated = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var parameter in descriptor.Parameters ?? [])
        {
            if (!arguments.TryGetValue(parameter.Name, out var value))
                continue;
            var singleParameterDescriptor = descriptor with { Parameters = [parameter] };
            var binding = ScriptParameterBinder.Bind(
                singleParameterDescriptor,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal) { [parameter.Name] = value },
                requireRequired: false);
            if (binding.Succeeded && binding.Arguments.TryGetValue(parameter.Name, out var normalized))
                migrated[parameter.Name] = normalized;
        }
        return migrated.ToImmutable();
    }

    private static string GetKey(ScriptLastArgumentsScope scope) =>
        $"{scope.ScriptId}\u001f{(int)scope.EntryKind}\u001f{(int?)scope.EditorTargetKind}";

    private sealed record ScriptLastArgumentsDocument(IReadOnlyList<ScriptLastArgumentsEntry> Entries);
}

public static class ScriptParameterSchemaFingerprint
{
    public static string Compute(ScriptDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var builder = new StringBuilder();
        builder.Append((int)descriptor.ApiVersion).Append('\n');
        foreach (var parameter in descriptor.Parameters ?? [])
        {
            builder.Append(parameter.Name).Append('\u001f')
                .Append((int)parameter.Type).Append('\u001f')
                .Append(parameter.Required ? '1' : '0').Append('\u001f');
            foreach (var choice in parameter.Choices ?? [])
                builder.Append(choice.Value).Append('\u001e');
            var constraints = parameter.Constraints;
            builder.Append('\u001f').Append(constraints?.Minimum)
                .Append('\u001f').Append(constraints?.Maximum)
                .Append('\u001f').Append(constraints?.Step)
                .Append('\u001f').Append(constraints?.MinLength)
                .Append('\u001f').Append(constraints?.MaxLength)
                .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
