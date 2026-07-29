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

public interface IAppDataDirectoryProvider
{
	string AppDataDirectory { get; }
}

public interface IAppVersionProvider
{
	string VersionString { get; }
	string BuildString { get; }
}

public interface IRemoteTransferSettings
{
	string BaseUrl { get; set; }
	string Username { get; set; }
	string Password { get; set; }
	bool VerifyByDownloading { get; set; }
	Task LoadSecretsAsync();
	FtpTarget GetFtpTarget(string fileName);
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

public interface IAsyncDelay
{
	Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IFtpClientFactory
{
	IFtpClient Create(FtpTarget target);
}

public interface IFtpClient : IDisposable
{
	Task ConnectAsync(CancellationToken cancellationToken);
	Task<long> GetFileSizeAsync(string remotePath, CancellationToken cancellationToken);
	Task<bool> UploadFileAsync(
		string localPath,
		string remotePath,
		bool resume,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken);
	Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken);
}

public sealed record FtpTarget(string Host, int Port, string RemotePath, Uri RemoteUri);
