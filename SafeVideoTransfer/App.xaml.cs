using Microsoft.Extensions.DependencyInjection;

namespace SafeVideoTransfer;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Resolve pages only after InitializeComponent has loaded App.xaml resources.
		var mainPage = _services.GetRequiredService<MainPage>();
		return new Window(new NavigationPage(mainPage));
	}
}
