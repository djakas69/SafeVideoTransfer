namespace SafeVideoTransfer.Services;

public sealed class MauiAppDataDirectoryProvider : IAppDataDirectoryProvider
{
	public string AppDataDirectory => FileSystem.AppDataDirectory;
}

public sealed class MauiAppVersionProvider : IAppVersionProvider
{
	public string VersionString => AppInfo.Current.VersionString;
	public string BuildString => AppInfo.Current.BuildString;
}
