using System.Text.Json.Serialization;

namespace SafeVideoTransfer.Models;

public sealed class VideoRecord
{
	public Guid Id { get; init; } = Guid.NewGuid();
	public string FileName { get; set; } = string.Empty;
	public string LocalPath { get; set; } = string.Empty;
	public long FileSizeBytes { get; set; }
	public TimeSpan Duration { get; set; }
	public string? Sha256 { get; set; }
	public string? RemoteUri { get; set; }
	public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
	public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
	public RecordingState RecordingState { get; set; }
	public UploadState UploadState { get; set; }
	public VerificationState VerificationState { get; set; }
	public DeletionState DeletionState { get; set; }
	public double UploadProgress { get; set; }
	public bool KeepLocal { get; set; }
	public string? LastError { get; set; }

	[JsonIgnore]
	public bool LocalFileExists => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);

	[JsonIgnore]
	public bool CanDelete => LocalFileExists && VerificationState == VerificationState.Verified;
}
