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
		if (!record.LocalFileExists)
			throw new FileNotFoundException("Local video is missing.", record.LocalPath);

		var target = settings.GetFtpTarget(record.FileName);
		record.RemoteUri = target.RemoteUri.AbsoluteUri;
		record.UploadState = UploadState.Uploading;
		record.VerificationState = VerificationState.NotStarted;
		record.LastError = null;
		await repository.UpsertAsync(record, cancellationToken);

		Exception? lastError = null;
		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			try
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
				var uploadProgress = new CallbackProgress<TransferProgress>(value =>
				{
					record.UploadProgress = record.FileSizeBytes == 0
						? 0
						: (double)value.BytesSent / record.FileSizeBytes;
					progress?.Report(value);
				});

				var succeeded = await client.UploadFileAsync(
					record.LocalPath,
					target.RemotePath,
					resume,
					uploadProgress,
					cancellationToken);

				if (!succeeded)
					throw new IOException("FTP upload did not complete successfully.");

				await MarkUploadedAsync(record, progress, cancellationToken);
				return;
			}
			catch (OperationCanceledException)
			{
				record.UploadState = UploadState.Interrupted;
				record.LastError = "FTP upload cancelled. Retry will resume the same remote file.";
				await repository.UpsertAsync(record, CancellationToken.None);
				throw;
			}
			catch (Exception ex)
			{
				lastError = ex;
				if (attempt == MaxAttempts)
					break;

				try
				{
					await delay.DelayAsync(
						TimeSpan.FromSeconds(Math.Pow(2, attempt)),
						cancellationToken);
				}
				catch (OperationCanceledException)
				{
					record.UploadState = UploadState.Interrupted;
					record.LastError = "FTP retry cancelled. The local file was kept.";
					await repository.UpsertAsync(record, CancellationToken.None);
					throw;
				}
			}
		}

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
