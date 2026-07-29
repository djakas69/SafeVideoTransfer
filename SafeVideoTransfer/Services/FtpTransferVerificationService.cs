using System.Security.Cryptography;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class FtpTransferVerificationService(
	IRemoteTransferSettings settings,
	IVideoStorageService storage,
	IVideoRecordRepository repository,
	IFtpClientFactory clientFactory) : ITransferVerificationService
{
	public async Task<VerificationResult> VerifyAsync(
		VideoRecord record,
		CancellationToken cancellationToken)
	{
		if (record.UploadState != UploadState.Uploaded || string.IsNullOrWhiteSpace(record.RemoteUri))
			return new VerificationResult(false, null, "Upload has not completed.");

		record.VerificationState = VerificationState.Verifying;
		await repository.UpsertAsync(record, cancellationToken);

		try
		{
			var target = settings.GetFtpTarget(record.FileName);
			using var client = clientFactory.Create(target);
			await client.ConnectAsync(cancellationToken);

			var remoteLength = await client.GetFileSizeAsync(target.RemotePath, cancellationToken);
			if (remoteLength != record.FileSizeBytes)
				return await FailAsync(
					record,
					remoteLength < 0 ? null : remoteLength,
					$"Remote length {remoteLength} does not match {record.FileSizeBytes}.");

			if (settings.VerifyByDownloading)
			{
				var localHash = record.Sha256 ??
					await storage.ComputeSha256Async(record, cancellationToken);
				await using var remoteStream =
					await client.OpenReadAsync(target.RemotePath, cancellationToken);
				var remoteHash = Convert.ToHexStringLower(
					await SHA256.HashDataAsync(remoteStream, cancellationToken));

				if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
					return await FailAsync(
						record,
						remoteLength,
						"Remote SHA-256 does not match the local file.");
			}

			record.VerificationState = VerificationState.Verified;
			record.LastError = null;
			await repository.UpsertAsync(record, cancellationToken);
			return new VerificationResult(
				true,
				remoteLength,
				settings.VerifyByDownloading
					? "FTP length and SHA-256 match."
					: "FTP file length matches.");
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return await FailAsync(record, null, ex.Message);
		}
	}

	private async Task<VerificationResult> FailAsync(
		VideoRecord record,
		long? length,
		string message)
	{
		record.VerificationState = VerificationState.Failed;
		record.LastError = message;
		await repository.UpsertAsync(record, CancellationToken.None);
		return new VerificationResult(false, length, message);
	}
}
