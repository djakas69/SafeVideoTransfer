using System.Windows.Input;

namespace SafeVideoTransfer.ViewModels;

public sealed class AsyncCommand(
	Func<Task> execute,
	Func<bool>? canExecute = null,
	Action<Exception>? onError = null) : ICommand
{
	private bool _isRunning;
	public event EventHandler? CanExecuteChanged;
	public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

	public async void Execute(object? parameter) =>
		await ExecuteAsync();

	public async Task ExecuteAsync()
	{
		if (!CanExecute(null)) return;
		_isRunning = true;
		RaiseCanExecuteChanged();
		try { await execute(); }
		catch (OperationCanceledException) { }
		catch (Exception ex) { onError?.Invoke(ex); }
		finally
		{
			_isRunning = false;
			RaiseCanExecuteChanged();
		}
	}

	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
