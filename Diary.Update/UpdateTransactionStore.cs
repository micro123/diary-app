using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Diary.Update;

public sealed class UpdateTransactionStore(ValidatedUpdatePlan validatedPlan)
{
    private readonly string _statusPath = Path.Combine(validatedPlan.TransactionDirectory, "status.json");
    private readonly string _journalPath = Path.Combine(validatedPlan.TransactionDirectory, "journal.jsonl");
    private readonly string _lockPath = Path.Combine(validatedPlan.TransactionDirectory, "transaction.lock");

    public string StatusPath => _statusPath;
    public string JournalPath => _journalPath;

    public ValueTask<FileStream> AcquireLockAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(validatedPlan.TransactionDirectory);
        try
        {
            return ValueTask.FromResult(new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("更新事务已经被另一个进程占用。", exception);
        }
    }

    public async ValueTask<UpdateTransactionStatus?> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statusPath))
            return null;
        await using var stream = new FileStream(
            _statusPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var status = await JsonSerializer.DeserializeAsync(
                stream,
                UpdateJson.Context.UpdateTransactionStatus,
                cancellationToken)
            ?? throw new InvalidDataException("更新事务状态文件为空。");
        if (!string.Equals(status.TransactionId, validatedPlan.Plan.TransactionId, StringComparison.Ordinal)
            || !string.Equals(
                status.TransactionTokenSha256,
                UpdateHash.ComputeSha256(validatedPlan.Plan.TransactionToken),
                StringComparison.Ordinal))
            throw new InvalidDataException("更新事务状态与计划令牌不匹配。");
        return status;
    }

    public ValueTask WriteStatusAsync(
        UpdateTransactionState state,
        string? message = null,
        CancellationToken cancellationToken = default) =>
        WriteAtomicJsonAsync(
            _statusPath,
            new UpdateTransactionStatus
            {
                TransactionId = validatedPlan.Plan.TransactionId,
                TransactionTokenSha256 = UpdateHash.ComputeSha256(validatedPlan.Plan.TransactionToken),
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = message,
            },
            UpdateJson.Context.UpdateTransactionStatus,
            cancellationToken);

    public async ValueTask AppendJournalAsync(
        UpdateJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(validatedPlan.TransactionDirectory);
        var line = JsonSerializer.Serialize(entry, UpdateJson.CompactContext.UpdateJournalEntry) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await using var stream = new FileStream(
            _journalPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<UpdateJournalEntry>> ReadJournalAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_journalPath))
            return [];
        var entries = new List<UpdateJournalEntry>();
        await using var stream = new FileStream(
            _journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            entries.Add(JsonSerializer.Deserialize(line, UpdateJson.Context.UpdateJournalEntry)
                ?? throw new InvalidDataException("更新事务日志包含空记录。"));
        }
        return entries;
    }

    public void ResetJournal()
    {
        Directory.CreateDirectory(validatedPlan.TransactionDirectory);
        using var stream = new FileStream(_journalPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    public static async ValueTask<UpdateTransactionPlan> LoadPlanAsync(
        string planPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            planPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
                stream,
                UpdateJson.Context.UpdateTransactionPlan,
                cancellationToken)
            ?? throw new InvalidDataException("更新事务计划为空。");
    }

    public static ValueTask WritePlanAsync(
        string planPath,
        UpdateTransactionPlan plan,
        CancellationToken cancellationToken = default) =>
        WriteAtomicJsonAsync(planPath, plan, UpdateJson.Context.UpdateTransactionPlan, cancellationToken);

    private static async ValueTask WriteAtomicJsonAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("目标 JSON 路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
