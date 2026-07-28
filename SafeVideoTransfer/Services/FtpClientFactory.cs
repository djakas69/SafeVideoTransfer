using FluentFTP;

namespace SafeVideoTransfer.Services;

internal static class FtpClientFactory
{
	public static AsyncFtpClient Create(RemoteTransferSettings settings, FtpTarget target)
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

		return new AsyncFtpClient(
			target.Host,
			settings.Username,
			settings.Password,
			target.Port,
			config,
			logger: null);
	}
}
