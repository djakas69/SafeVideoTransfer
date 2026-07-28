namespace SafeVideoTransfer.Services;

public sealed class UserConfirmationService : IUserConfirmationService
{
	public async Task<bool> ConfirmAsync(
		string title, string message, string accept, string cancel)
	{
		var page = Application.Current?.Windows.FirstOrDefault()?.Page
			?? throw new InvalidOperationException("The application window is not available.");

		return await page.DisplayAlertAsync(title, message, accept, cancel);
	}
}
