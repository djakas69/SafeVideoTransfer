using FluentFTP;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class FtpVideoTransferService(
	RemoteTransferSettings settings,
	IVideoRecordRepository repository) : IVideoTransferService
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
				using var client = FtpClientFactory.Create(settings, target);
				await client.Connect(cancellationToken);

				var remoteSize = await client.GetFileSize(target.RemotePath, -1, cancellationToken);
				if (remoteSize == record.FileSizeBytes)
				{
					await MarkUploadedAsync(record, progress, cancellationToken);
					return;
				}

				var existsMode = remoteSize > 0 && remoteSize < record.FileSizeBytes
					? FtpRemoteExists.Resume
					: FtpRemoteExists.Overwrite;

				var ftpProgress = new Progress<FtpProgress>(value =>
				{
					var bytesSent = value.TransferredBytes;
					if (value.Progress >= 0)
						bytesSent = (long)(record.FileSizeBytes * value.Progress / 100d);
					bytesSent = Math.Clamp(bytesSent, 0, record.FileSizeBytes);
					record.UploadProgress = record.FileSizeBytes == 0
						? 0
						: (double)bytesSent / record.FileSizeBytes;
					progress?.Report(new TransferProgress(bytesSent, record.FileSizeBytes));
				});

				var status = await client.UploadFile(
					record.LocalPath,
					target.RemotePath,
					existsMode,
					createRemoteDir: true,
					FtpVerify.None,
					ftpProgress,
					cancellationToken);

				if (status != FtpStatus.Success)
					throw new IOException($"FTP upload returned status {status}.");

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
					await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
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
}
