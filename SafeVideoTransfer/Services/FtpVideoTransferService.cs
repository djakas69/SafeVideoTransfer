using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class FtpVideoTransferService(
	IRemoteTransferSettings settings,
	IVideoRecordRepository repository,
	IFtpClientFactory clientFactory,
	IAsyncDelay delay) : IVideoTransferService
{
	private const int MaxAttempts = 3;

	public async Task UploadAsync(
		VideoRecord record,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		EnsureLocalFileExists(record);
		var target = settings.GetFtpTarget(record.FileName);
		await MarkUploadingAsync(record, target, cancellationToken);

		Exception? lastError = null;
		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			try
			{
				await UploadAttemptAsync(record, target, progress, cancellationToken);
				return;
			}
			catch (OperationCanceledException)
			{
				await MarkInterruptedAsync(
					record,
					"FTP upload cancelled. Retry will resume the same remote file.");
				throw;
			}
			catch (Exception ex) when (attempt < MaxAttempts)
			{
				lastError = ex;
				await WaitBeforeRetryAsync(record, attempt, cancellationToken);
			}
			catch (Exception ex)
			{
				lastError = ex;
			}
		}

		await ThrowUploadFailedAsync(record, lastError);
	}

	private static void EnsureLocalFileExists(VideoRecord record)
	{
		if (!record.LocalFileExists)
			throw new FileNotFoundException("Local video is missing.", record.LocalPath);
	}

	private async Task MarkUploadingAsync(
		VideoRecord record,
		FtpTarget target,
		CancellationToken cancellationToken)
	{
		record.RemoteUri = target.RemoteUri.AbsoluteUri;
		record.UploadState = UploadState.Uploading;
		record.VerificationState = VerificationState.NotStarted;
		record.LastError = null;
		await repository.UpsertAsync(record, cancellationToken);
	}

	private async Task UploadAttemptAsync(
		VideoRecord record,
		FtpTarget target,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		using var client = clientFactory.Create(target);
		await client.ConnectAsync(cancellationToken);

		var remoteSize = await client.GetFileSizeAsync(target.RemotePath, cancellationToken);
		if (remoteSize == record.FileSizeBytes)
		{
			await MarkUploadedAsync(record, progress, cancellationToken);
			return;
		}

		var resume = remoteSize > 0 && remoteSize < record.FileSizeBytes;
		var succeeded = await client.UploadFileAsync(
			record.LocalPath,
			target.RemotePath,
			resume,
			CreateUploadProgress(record, progress),
			cancellationToken);

		if (!succeeded)
			throw new IOException("FTP upload did not complete successfully.");

		await MarkUploadedAsync(record, progress, cancellationToken);
	}

	private static IProgress<TransferProgress> CreateUploadProgress(
		VideoRecord record,
		IProgress<TransferProgress>? progress) =>
		new CallbackProgress<TransferProgress>(value =>
		{
			record.UploadProgress = record.FileSizeBytes == 0
				? 0
				: (double)value.BytesSent / record.FileSizeBytes;
			progress?.Report(value);
		});

	private async Task WaitBeforeRetryAsync(
		VideoRecord record,
		int attempt,
		CancellationToken cancellationToken)
	{
		try
		{
			await delay.DelayAsync(
				TimeSpan.FromSeconds(Math.Pow(2, attempt)),
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			await MarkInterruptedAsync(record, "FTP retry cancelled. The local file was kept.");
			throw;
		}
	}

	private async Task MarkInterruptedAsync(VideoRecord record, string error)
	{
		record.UploadState = UploadState.Interrupted;
		record.LastError = error;
		await repository.UpsertAsync(record, CancellationToken.None);
	}

	private async Task ThrowUploadFailedAsync(VideoRecord record, Exception? lastError)
	{
		record.UploadState = UploadState.Failed;
		record.LastError = lastError?.Message;
		await repository.UpsertAsync(record, CancellationToken.None);
		throw new IOException($"FTP upload failed after {MaxAttempts} attempts.", lastError);
	}

	private async Task MarkUploadedAsync(
		VideoRecord record,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		record.UploadState = UploadState.Uploaded;
		record.UploadProgress = 1;
		await repository.UpsertAsync(record, cancellationToken);
		progress?.Report(new TransferProgress(record.FileSizeBytes, record.FileSizeBytes));
	}

	private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
	{
		public void Report(T value) => callback(value);
	}
}
