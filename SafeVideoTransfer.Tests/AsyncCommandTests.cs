using SafeVideoTransfer.ViewModels;

namespace SafeVideoTransfer.Tests;

public sealed class AsyncCommandTests
{
	[Fact]
	public void CanExecute_PredicateIsFalse_ReturnsFalse()
	{
		var command = new AsyncCommand(() => Task.CompletedTask, () => false);

		var canExecute = command.CanExecute(null);

		Assert.False(canExecute);
	}

	[Fact]
	public async Task ExecuteAsync_WhileRunning_PreventsConcurrentExecution()
	{
		var release = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		var command = new AsyncCommand(async () =>
		{
			calls++;
			started.TrySetResult();
			await release.Task;
		});

		var first = command.ExecuteAsync();
		await started.Task;
		await command.ExecuteAsync();
		release.SetResult();
		await first;

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task ExecuteAsync_RaisesCanExecuteChangedAtStartAndCompletion()
	{
		var release = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var started = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var notifications = 0;
		var command = new AsyncCommand(async () =>
		{
			started.SetResult();
			await release.Task;
		});
		command.CanExecuteChanged += (_, _) => notifications++;

		var execution = command.ExecuteAsync();
		await started.Task;
		Assert.False(command.CanExecute(null));
		release.SetResult();
		await execution;

		Assert.True(command.CanExecute(null));
		Assert.Equal(2, notifications);
	}

	[Fact]
	public async Task ExecuteAsync_Exception_ReportsErrorAndReenablesCommand()
	{
		Exception? reported = null;
		var expected = new InvalidOperationException("failure");
		var command = new AsyncCommand(
			() => Task.FromException(expected),
			onError: exception => reported = exception);

		await command.ExecuteAsync();

		Assert.Same(expected, reported);
		Assert.True(command.CanExecute(null));
	}

	[Fact]
	public async Task ExecuteAsync_Cancellation_DoesNotReportError()
	{
		var reported = false;
		var command = new AsyncCommand(
			() => Task.FromCanceled(new CancellationToken(canceled: true)),
			onError: _ => reported = true);

		await command.ExecuteAsync();

		Assert.False(reported);
		Assert.True(command.CanExecute(null));
	}

	[Fact]
	public void RaiseCanExecuteChanged_NotifiesSubscribers()
	{
		var notifications = 0;
		var command = new AsyncCommand(() => Task.CompletedTask);
		command.CanExecuteChanged += (_, _) => notifications++;

		command.RaiseCanExecuteChanged();

		Assert.Equal(1, notifications);
	}
}
