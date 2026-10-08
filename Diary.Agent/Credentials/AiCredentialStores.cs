using Diary.Core.Configure;
using Diary.Core.Utils;
using Newtonsoft.Json.Linq;

namespace Diary.Agent.Credentials;

public sealed class CredentialValue
{
    private readonly string _value;

    public CredentialValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    public ReadOnlyMemory<char> Memory => _value.AsMemory();

    public string Reveal() => _value;

    public override string ToString() => "********";
}

public interface IAiCredentialStore
{
    ValueTask<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default);

    ValueTask<CredentialValue?> GetAsync(string reference, CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        string reference,
        ReadOnlyMemory<char> value,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

public sealed class AiCredentialStore : IAiCredentialStore
{
    private const string EnvironmentPrefix = "env:";
    private const string MemoryPrefix = "memory:";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly CredentialDocument _persistent = new();
    private readonly bool _persistentReadable;

    public AiCredentialStore()
    {
        var status = EasySaveLoad.LoadJson(_persistent, out var json, out _);
        _persistentReadable = status != ConfigurationLoadStatus.Unreadable;
        if (status == ConfigurationLoadStatus.Loaded)
        {
            var loaded = json.ToObject<CredentialDocument>();
            if (loaded is not null)
                _persistent.Values = new Dictionary<string, string>(loaded.Values, StringComparer.Ordinal);
        }
    }

    public async ValueTask<bool> ExistsAsync(
        string reference,
        CancellationToken cancellationToken = default)
        => await GetAsync(reference, cancellationToken) is not null;

    public async ValueTask<CredentialValue?> GetAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        if (reference.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
        {
            var value = Environment.GetEnvironmentVariable(reference[EnvironmentPrefix.Length..]);
            return value is null ? null : new CredentialValue(value);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var source = reference.StartsWith(MemoryPrefix, StringComparison.Ordinal)
                ? _memory
                : _persistent.Values;
            return source.TryGetValue(reference, out var value) ? new CredentialValue(value) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetAsync(
        string reference,
        ReadOnlyMemory<char> value,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        if (reference.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("环境变量凭据是只读的。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (reference.StartsWith(MemoryPrefix, StringComparison.Ordinal))
            {
                _memory[reference] = value.ToString();
                return;
            }
            EnsurePersistentReadable();
            _persistent.Values[reference] = value.ToString();
            SavePersistent();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        if (reference.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("环境变量凭据是只读的。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (reference.StartsWith(MemoryPrefix, StringComparison.Ordinal))
            {
                _memory.Remove(reference);
                return;
            }
            EnsurePersistentReadable();
            if (_persistent.Values.Remove(reference))
                SavePersistent();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > 300
            || reference.Contains('\r')
            || reference.Contains('\n'))
        {
            throw new ArgumentException("凭据引用无效。", nameof(reference));
        }
        if (!reference.StartsWith(EnvironmentPrefix, StringComparison.Ordinal)
            && !reference.StartsWith(MemoryPrefix, StringComparison.Ordinal)
            && !reference.StartsWith("diary.ai/", StringComparison.Ordinal))
        {
            throw new ArgumentException("持久化凭据引用必须使用 diary.ai/ 前缀。", nameof(reference));
        }
    }

    private void EnsurePersistentReadable()
    {
        if (!_persistentReadable)
            throw new InvalidOperationException("加密凭据文件不可读取，已阻止覆盖。");
    }

    private void SavePersistent()
    {
        if (!EasySaveLoad.SaveJson(_persistent, JObject.FromObject(_persistent)))
            throw new IOException("无法保存 AI 加密凭据。");
    }

    [StorageFile("ai-agent-credentials.dat", "Diary.Agent.Credentials.v1")]
    private sealed class CredentialDocument
    {
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    }
}

public static class AiCredentialReferenceCleanup
{
    public static async ValueTask<int> DeleteUnusedLocalReferencesAsync(
        IAiCredentialStore store,
        IEnumerable<string> removedReferences,
        IEnumerable<string> retainedReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(removedReferences);
        ArgumentNullException.ThrowIfNull(retainedReferences);
        var retained = retainedReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var deleted = 0;
        foreach (var reference in removedReferences
                     .Where(reference => !string.IsNullOrWhiteSpace(reference))
                     .Select(reference => reference.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .Where(reference => !retained.Contains(reference))
                     .Where(reference => reference.StartsWith("diary.ai/", StringComparison.Ordinal)
                                         || reference.StartsWith("memory:", StringComparison.Ordinal)))
        {
            await store.DeleteAsync(reference, cancellationToken);
            deleted++;
        }
        return deleted;
    }
}
