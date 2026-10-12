namespace Diary.Core.ConfigurationTools;

public sealed record ConfigurationFileDescriptor(
    string Id,
    string DisplayName,
    string Category,
    string RelativePath,
    string? EncryptionPurpose = null,
    string Description = "")
{
    public bool IsEncrypted => !string.IsNullOrEmpty(EncryptionPurpose);
}

public enum ConfigurationDocumentStatus
{
    Missing,
    Loaded,
    Unreadable,
}

public enum ConfigurationMasterKeyStatus
{
    Missing,
    Readable,
    Unreadable,
}

public sealed record ConfigurationMasterKeyDiagnostic(
    ConfigurationMasterKeyStatus Status,
    string Message);

public sealed record ConfigurationMasterKeyResetResult(
    string SnapshotDirectory,
    string RetainedMasterKeyPath);

public sealed record ConfigurationDocument(
    ConfigurationFileDescriptor Descriptor,
    ConfigurationDocumentStatus Status,
    string? Json,
    string? Fingerprint,
    DateTimeOffset? LastWriteTime,
    bool HasBackup,
    string Format,
    string? Error = null)
{
    public bool CanEdit => Status == ConfigurationDocumentStatus.Loaded;
}
