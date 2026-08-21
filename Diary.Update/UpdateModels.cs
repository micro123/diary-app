namespace Diary.Update;

public enum UpdateFileOperationKind
{
    Add,
    Replace,
    Delete,
}

public enum UpdateTransactionState
{
    Created,
    Downloading,
    ReadyToApply,
    WaitingForExit,
    HandoffPrepared,
    HandingOff,
    Applying,
    Applied,
    Restarted,
    RollingBack,
    RolledBack,
    Failed,
}

public enum UpdateJournalPhase
{
    Prepared,
    Completed,
}

public sealed record UpdateFileOperation
{
    public required UpdateFileOperationKind Kind { get; init; }
    public required string TargetPath { get; init; }
    public string? SourcePath { get; init; }
    public long? SourceSize { get; init; }
    public string? SourceSha256 { get; init; }
    public string? ExistingSha256 { get; init; }
    public bool Executable { get; init; }
}

public sealed record UpdateUpdaterDescriptor
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required string Rid { get; init; }
    public required int ProtocolVersion { get; init; }
}

public sealed record UpdateRestartOptions
{
    public required string ExecutablePath { get; init; }
    public required string Sha256 { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public sealed record UpdateTransactionPlan
{
    public int SchemaVersion { get; init; } = UpdateProtocol.PlanSchemaVersion;
    public required string TransactionId { get; init; }
    public required string TransactionToken { get; init; }
    public required string HandoffToken { get; init; }
    public required string CurrentVersion { get; init; }
    public required string TargetVersion { get; init; }
    public required string Rid { get; init; }
    public required string InstallDirectory { get; init; }
    public required string UpdatesRootDirectory { get; init; }
    public required string TransactionDirectory { get; init; }
    public required string StagingDirectory { get; init; }
    public required string BackupDirectory { get; init; }
    public int MinUpdaterVersion { get; init; } = UpdateProtocol.UpdaterProtocolVersion;
    public int WaitForExitTimeoutSeconds { get; init; } = 120;
    public IReadOnlyList<UpdateFileOperation> Operations { get; init; } = [];
    public required string InstalledManifestSourcePath { get; init; }
    public required long InstalledManifestSize { get; init; }
    public required string InstalledManifestSha256 { get; init; }
    public required UpdateUpdaterDescriptor BootstrapUpdater { get; init; }
    public required UpdateUpdaterDescriptor TargetUpdater { get; init; }
    public UpdateRestartOptions? Restart { get; init; }
}

public sealed record UpdateTransactionStatus
{
    public required string TransactionId { get; init; }
    public required string TransactionTokenSha256 { get; init; }
    public required UpdateTransactionState State { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? Message { get; init; }
}

public sealed record UpdateJournalEntry
{
    public required int Sequence { get; init; }
    public required UpdateJournalPhase Phase { get; init; }
    public required UpdateFileOperationKind Kind { get; init; }
    public required string TargetPath { get; init; }
    public string? BackupPath { get; init; }
    public required bool ExistedBefore { get; init; }
    public int? OriginalUnixMode { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record UpdateApplyResult(UpdateTransactionState State, bool RestartStarted);

public sealed record UpdateMachineVersion(int ProtocolVersion, string Rid);
