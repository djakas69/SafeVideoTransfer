using SafeVideoTransfer.Services;

namespace SafeVideoTransfer.IntegrationTests.Support;

internal sealed class IntegrationSettings(Uri baseUri) : IRemoteTransferSettings
{
	public string BaseUrl { get; set; } = baseUri.AbsoluteUri;
	public string Username { get; set; } = "integration-user";
	public string Password { get; set; } = "integration-password";
	public bool VerifyByDownloading { get; set; } = true;
	public Task LoadSecretsAsync() => Task.CompletedTask;

	public FtpTarget GetFtpTarget(string fileName)
	{
		var baseUri = new Uri(BaseUrl);
		var folder = Uri.UnescapeDataString(baseUri.AbsolutePath).TrimEnd('/');
		var remotePath = $"{folder}/{fileName}";
		var port = baseUri.IsDefaultPort ? 21 : baseUri.Port;
		return new FtpTarget(
			baseUri.Host,
			port,
			remotePath,
			new UriBuilder(Uri.UriSchemeFtp, baseUri.Host, port, remotePath).Uri);
	}
}

internal sealed class IntegrationAppDataProvider(string path) : IAppDataDirectoryProvider
{
	public string AppDataDirectory { get; } = path;
}

internal sealed class ImmediateDelay : IAsyncDelay
{
	public int Calls { get; private set; }

	public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
	{
		Calls++;
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}
}
