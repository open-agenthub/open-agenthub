namespace AgentHub.Api.Files;

public enum SessionFileStorageKind
{
    S3,
    Pod,
}

public enum SessionFileState
{
    Reserved,
    Uploading,
    Ready,
    Failed,
    Expired,
    Deleted,
}

public enum SessionFilePreviewState
{
    None,
    Queued,
    Converting,
    Ready,
    Failed,
}

public sealed record SessionFileRecord(
    string Id,
    string SessionId,
    string Owner,
    string Name,
    string Extension,
    string DeclaredMimeType,
    string? DetectedMimeType,
    long Size,
    SessionFileStorageKind StorageKind,
    string StorageLocator,
    SessionFileState State,
    SessionFilePreviewState PreviewState,
    string? PreviewFileId,
    string Creator,
    string Source,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateTime? ExpiresAt);

public sealed record SessionFilePresentation(
    string SessionId,
    string? FileId,
    long Revision,
    string Presenter,
    DateTime UpdatedAt);

public sealed record SessionFileUsage(int Count, long Bytes);

public sealed record SessionFileCapabilities(
    string StorageMode,
    bool UploadAvailable,
    IReadOnlyList<string> DirectPreviewMimeTypes,
    IReadOnlyDictionary<string, string> UnavailableReasons,
    bool OfficePreviewEnabled,
    string OfficePreviewStatus,
    SessionFileOptions Limits);

public sealed record SessionFileValidationResult(
    bool Allowed,
    string? Code = null,
    string? DetectedMimeType = null)
{
    public static SessionFileValidationResult Accept(string? detectedMimeType = null) =>
        new(true, DetectedMimeType: detectedMimeType);

    public static SessionFileValidationResult Reject(string code) => new(false, code);
}
