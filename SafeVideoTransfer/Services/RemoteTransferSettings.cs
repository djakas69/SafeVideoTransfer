namespace SafeVideoTransfer.Services;

public sealed class RemoteTransferSettings : IRemoteTransferSettings
{
	public const string DefaultBaseUrl = "ftp://192.168.178.40/Public/recording/";

	private string _password = string.Empty;

	public string BaseUrl
	{
		get
		{
			var storedValue = Preferences.Default.Get(nameof(BaseUrl), DefaultBaseUrl);
			return string.IsNullOrWhiteSpace(storedValue) ||
			       storedValue is "http://192.168.178.40/recording/"
				       or "http://192.168.178.40:8080/Public/recording/"
				? DefaultBaseUrl
				: storedValue;
		}
		set => Preferences.Default.Set(nameof(BaseUrl), value.Trim());
	}

	public string Username
	{
		get => Preferences.Default.Get(nameof(Username), string.Empty);
		set => Preferences.Default.Set(nameof(Username), value);
	}

	public string Password
	{
		get => _password;
		set
		{
			_password = value;
			_ = SecureStorage.Default.SetAsync(nameof(Password), value);
		}
	}

	public async Task LoadSecretsAsync() =>
		_password = await SecureStorage.Default.GetAsync(nameof(Password)) ?? string.Empty;

	// A GET-based SHA-256 verification is slower, but verifies content rather than only length.
	public bool VerifyByDownloading
	{
		get => Preferences.Default.Get(nameof(VerifyByDownloading), true);
		set => Preferences.Default.Set(nameof(VerifyByDownloading), value);
	}

	public FtpTarget GetFtpTarget(string fileName)
	{
		if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) ||
		    baseUri.Scheme != Uri.UriSchemeFtp)
			throw new InvalidOperationException("Enter a valid FTP upload folder URL.");
		if (string.IsNullOrWhiteSpace(Username))
			throw new InvalidOperationException("Enter the WD My Cloud username.");
		if (string.IsNullOrWhiteSpace(Password))
			throw new InvalidOperationException("Enter the WD My Cloud password.");

		var folder = Uri.UnescapeDataString(baseUri.AbsolutePath).TrimEnd('/');
		var remotePath = $"{folder}/{fileName}";
		var port = baseUri.IsDefaultPort ? 21 : baseUri.Port;
		var remoteUri = new UriBuilder(Uri.UriSchemeFtp, baseUri.Host, port, remotePath).Uri;
		return new FtpTarget(baseUri.Host, port, remotePath, remoteUri);
	}
}
