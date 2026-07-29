using FluentFTP;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class FluentFtpClientFactory(IRemoteTransferSettings settings) : IFtpClientFactory
{
	public IFtpClient Create(FtpTarget target)
	{
		var config = new FtpConfig
		{
			EncryptionMode = FtpEncryptionMode.None,
			DataConnectionType = FtpDataConnectionType.AutoPassive,
			ConnectTimeout = 10_000,
			ReadTimeout = 30_000,
			WriteTimeout = 30_000,
			DataConnectionConnectTimeout = 10_000,
			DataConnectionReadTimeout = 30_000,
			RetryAttempts = 1
		};

		var client = new AsyncFtpClient(
			target.Host,
			settings.Username,
			settings.Password,
			target.Port,
			config,
			logger: null);
		return new FluentFtpClient(client);
	}
}

internal sealed class FluentFtpClient(AsyncFtpClient client) : IFtpClient
{
	public Task ConnectAsync(CancellationToken cancellationToken) =>
		client.Connect(cancellationToken);

	public Task<long> GetFileSizeAsync(
		string remotePath, CancellationToken cancellationToken) =>
		client.GetFileSize(remotePath, -1, cancellationToken);

	public async Task<bool> UploadFileAsync(
		string localPath,
		string remotePath,
		bool resume,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		var totalBytes = new FileInfo(localPath).Length;
		var ftpProgress = new Progress<FtpProgress>(value =>
		{
			var bytesSent = value.TransferredBytes;
			if (value.Progress >= 0)
				bytesSent = (long)(totalBytes * value.Progress / 100d);
			progress?.Report(new TransferProgress(
				Math.Clamp(bytesSent, 0, totalBytes),
				totalBytes));
		});

		var status = await client.UploadFile(
			localPath,
			remotePath,
			resume ? FtpRemoteExists.Resume : FtpRemoteExists.Overwrite,
			createRemoteDir: true,
			FtpVerify.None,
			ftpProgress,
			cancellationToken);
		return status == FtpStatus.Success;
	}

	public async Task<Stream> OpenReadAsync(
		string remotePath, CancellationToken cancellationToken) =>
		await client.OpenRead(
			remotePath,
			FtpDataType.Binary,
			restart: 0,
			checkIfFileExists: true,
			cancellationToken);

	public void Dispose() => client.Dispose();
}
