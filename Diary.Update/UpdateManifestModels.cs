namespace Diary.Update;

public sealed record UpdateManifestFile
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required string Component { get; init; }
    public bool Executable { get; init; }
}

public sealed record UpdateManifest
{
    public int ManifestFormatVersion { get; init; }
    public required string VersionId { get; init; }
    public required long Sequence { get; init; }
    public required string DataVersion { get; init; }
    public required string Channel { get; init; }
    public required string Rid { get; init; }
    public required string Flavor { get; init; }
    public int MinUpdaterVersion { get; init; }
    public long MinIncrementalSequence { get; init; }
    public required string ManifestContentId { get; init; }
    public IReadOnlyList<UpdateManifestFile> Files { get; init; } = [];
}

public sealed record UpdatePackageDescriptor
{
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record UpdateManifestEnvelope
{
    public required UpdateManifest Manifest { get; init; }
    public required UpdatePackageDescriptor FullPackage { get; init; }
}

public sealed record UpdateCheckRequest(
    Uri ServerUri,
    string Channel,
    string Rid,
    string Flavor,
    long CurrentSequence);

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    NoPublishedVersion,
    UnsupportedUpdater,
    TemporarilyUnavailable,
    InvalidResponse,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateManifestEnvelope? Envelope = null,
    Uri? FullPackageUri = null,
    string? Error = null);
