using Microsoft.Extensions.Logging;
using SafeVideoTransfer.Services;
using SafeVideoTransfer.ViewModels;

namespace SafeVideoTransfer;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		builder.Services.AddSingleton<RemoteTransferSettings>();
		builder.Services.AddSingleton<IRemoteTransferSettings>(
			sp => sp.GetRequiredService<RemoteTransferSettings>());
		builder.Services.AddSingleton<IAppDataDirectoryProvider, MauiAppDataDirectoryProvider>();
		builder.Services.AddSingleton<IAppVersionProvider, MauiAppVersionProvider>();
		builder.Services.AddSingleton(TimeProvider.System);
		builder.Services.AddSingleton<IAsyncDelay, SystemAsyncDelay>();
		builder.Services.AddSingleton<IFtpClientFactory, FluentFtpClientFactory>();
		builder.Services.AddSingleton<IVideoRecordRepository, JsonVideoRecordRepository>();
		builder.Services.AddSingleton<IVideoStorageService, VideoStorageService>();
		builder.Services.AddSingleton<IVideoTransferService, FtpVideoTransferService>();
		builder.Services.AddSingleton<ITransferVerificationService, FtpTransferVerificationService>();
		builder.Services.AddSingleton<IVideoRecordingService, IosVideoRecordingService>();
		builder.Services.AddSingleton<IUserConfirmationService, UserConfirmationService>();
		builder.Services.AddSingleton<MainPageViewModel>();
		builder.Services.AddSingleton<MainPage>();

		return builder.Build();
	}
}
