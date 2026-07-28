using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public interface IVideoRecordingService
{
	Task<VideoRecordingResult?> RecordAsync(string destinationPath, CancellationToken cancellationToken);
}

public interface IVideoStorageService
{
	string CreateSafeVideoPath();
	Task<VideoRecord> RegisterRecordingAsync(string path, TimeSpan duration, CancellationToken cancellationToken);
	Task DeleteLocalAsync(VideoRecord record, CancellationToken cancellationToken);
	Task DiscardLocalAsync(VideoRecord record, CancellationToken cancellationToken);
	Task<IReadOnlyList<VideoRecord>> RecoverAsync(CancellationToken cancellationToken);
	Task<string> ComputeSha256Async(VideoRecord record, CancellationToken cancellationToken);
}

public interface IUserConfirmationService
{
	Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}

public interface IVideoRecordRepository
{
	Task<IReadOnlyList<VideoRecord>> GetAllAsync(CancellationToken cancellationToken);
	Task UpsertAsync(VideoRecord record, CancellationToken cancellationToken);
}

public interface IVideoTransferService
{
	Task UploadAsync(VideoRecord record, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
}

public interface ITransferVerificationService
{
	Task<VerificationResult> VerifyAsync(VideoRecord record, CancellationToken cancellationToken);
}
