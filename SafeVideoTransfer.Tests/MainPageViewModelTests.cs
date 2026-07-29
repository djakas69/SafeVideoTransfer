using SafeVideoTransfer.Models;
using SafeVideoTransfer.Tests.TestSupport;
using SafeVideoTransfer.ViewModels;

namespace SafeVideoTransfer.Tests;

public sealed class MainPageViewModelTests
{
	[Fact]
	public void Constructor_InitialState_IsReadyAndCommandsReflectNoSelection()
	{
		using var context = new ViewModelContext();

		var viewModel = context.CreateViewModel();

		Assert.Equal("Ready", viewModel.StatusMessage);
		Assert.Equal("—", viewModel.FileName);
		Assert.Equal("Version 1.2.3 (build 42)", viewModel.AppVersion);
		Assert.Empty(viewModel.Records);
		Assert.True(viewModel.RecordCommand.CanExecute(null));
		Assert.False(viewModel.UploadCommand.CanExecute(null));
		Assert.False(viewModel.DeleteCommand.CanExecute(null));
		Assert.False(viewModel.CancelCommand.CanExecute(null));
	}

	[Fact]
	public async Task InitializeAsync_NoSavedVideos_SetsReadyToRecord()
	{
		using var context = new ViewModelContext();
		var viewModel = context.CreateViewModel();

		await viewModel.InitializeAsync();

		Assert.Equal("Ready to record.", viewModel.StatusMessage);
		Assert.Equal(1, context.Settings.LoadSecretsCalls);
		Assert.Null(viewModel.Current);
	}

	[Fact]
	public async Task RecordAsync_Success_AddsRecordedVideoAndStartsTransfer()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RegisteredRecord = record;
		context.Verification.Handler = (candidate, _) =>
		{
			candidate.VerificationState = VerificationState.Failed;
			return Task.FromResult(new VerificationResult(false, null, "not verified"));
		};
		var viewModel = context.CreateViewModel();

		await viewModel.RecordCommand.ExecuteAsync();
		await viewModel.WaitForTransfersAsync();

		Assert.Contains(record, viewModel.Records);
		Assert.Same(record, viewModel.Current);
		Assert.Equal(1, context.Recording.Calls);
		Assert.Equal(1, context.Transfer.Calls);
		Assert.Equal(RecordingState.Recorded, record.RecordingState);
	}

	[Fact]
	public async Task RecordAsync_UserCancels_DoesNotAddOrUploadVideo()
	{
		using var context = new ViewModelContext();
		context.Recording.Handler = (_, _) =>
			Task.FromResult<VideoRecordingResult?>(null);
		var viewModel = context.CreateViewModel();

		await viewModel.RecordCommand.ExecuteAsync();

		Assert.Empty(viewModel.Records);
		Assert.Equal(0, context.Transfer.Calls);
		Assert.Equal("Recording cancelled.", viewModel.StatusMessage);
	}

	[Fact]
	public async Task RecordAsync_ServiceFails_ShowsErrorAndDoesNotUpload()
	{
		using var context = new ViewModelContext();
		context.Recording.Handler = (_, _) =>
			Task.FromException<VideoRecordingResult?>(new InvalidOperationException("camera failed"));
		var viewModel = context.CreateViewModel();

		await viewModel.RecordCommand.ExecuteAsync();

		Assert.Empty(viewModel.Records);
		Assert.Equal(0, context.Transfer.Calls);
		Assert.Contains("camera failed", viewModel.StatusMessage);
		Assert.False(viewModel.IsBusy);
	}

	[Fact]
	public async Task InitializeAsync_LocalRecoveredVideo_EnablesUploadCommand()
	{
		using var context = new ViewModelContext();
		context.Storage.RecoveredRecords.Add(context.CreateLocalRecord());
		var viewModel = context.CreateViewModel();

		await viewModel.InitializeAsync();

		Assert.True(viewModel.UploadCommand.CanExecute(null));
	}

	[Fact]
	public async Task UploadAsync_VerificationSucceeds_DeletesLocalFileAndRemovesRecord()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RecoveredRecords.Add(record);
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();

		await viewModel.UploadCommand.ExecuteAsync();
		await viewModel.WaitForTransfersAsync();

		Assert.Equal(1, context.Storage.DeleteCalls);
		Assert.False(File.Exists(record.LocalPath));
		Assert.Empty(viewModel.Records);
		Assert.Null(viewModel.Current);
		Assert.Equal(0, context.Confirmation.Calls);
		Assert.Contains("Local file deleted", viewModel.StatusMessage);
	}

	[Fact]
	public async Task UploadAsync_TransferFails_DoesNotVerifyOrDeleteLocalFile()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RecoveredRecords.Add(record);
		context.Transfer.Handler = (_, _, _) =>
			Task.FromException(new IOException("FTP unavailable"));
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();

		await viewModel.UploadCommand.ExecuteAsync();
		await viewModel.WaitForTransfersAsync();

		Assert.Equal(0, context.Verification.Calls);
		Assert.Equal(0, context.Storage.DeleteCalls);
		Assert.True(File.Exists(record.LocalPath));
		Assert.Contains(record, viewModel.Records);
		Assert.Equal("FTP unavailable", record.LastError);
		Assert.Contains("FTP unavailable", viewModel.StatusMessage);
	}

	[Fact]
	public async Task UploadAsync_VerificationFails_DoesNotDeleteLocalFile()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RecoveredRecords.Add(record);
		context.Verification.Handler = (candidate, _) =>
		{
			candidate.VerificationState = VerificationState.Failed;
			return Task.FromResult(new VerificationResult(false, 12, "hash mismatch"));
		};
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();

		await viewModel.UploadCommand.ExecuteAsync();
		await viewModel.WaitForTransfersAsync();

		Assert.Equal(0, context.Storage.DeleteCalls);
		Assert.True(File.Exists(record.LocalPath));
		Assert.Contains(record, viewModel.Records);
		Assert.Contains("hash mismatch", viewModel.StatusMessage);
	}

	[Fact]
	public async Task UploadAsync_CancelledDuringTransfer_KeepsLocalFileAndReportsInterrupted()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RecoveredRecords.Add(record);
		var started = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		context.Transfer.Handler = async (_, _, token) =>
		{
			started.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
		};
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();

		await viewModel.UploadCommand.ExecuteAsync();
		await started.Task;
		Assert.True(viewModel.CancelCommand.CanExecute(null));
		await viewModel.CancelCommand.ExecuteAsync();
		await viewModel.WaitForTransfersAsync();

		Assert.Equal(0, context.Storage.DeleteCalls);
		Assert.True(File.Exists(record.LocalPath));
		Assert.Contains("interrupted", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AutomaticUpload_VerificationFails_NeverDeletesBeforeVerification()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RegisteredRecord = record;
		context.Verification.Handler = (_, _) =>
			Task.FromResult(new VerificationResult(false, null, "remote incomplete"));
		var viewModel = context.CreateViewModel();

		await viewModel.RecordCommand.ExecuteAsync();
		await viewModel.WaitForTransfersAsync();

		Assert.Equal(0, context.Storage.DeleteCalls);
		Assert.True(File.Exists(record.LocalPath));
	}

	[Fact]
	public async Task ManualDelete_VerifiedAndConfirmed_DeletesAndRemovesRecord()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		record.VerificationState = VerificationState.Verified;
		context.Storage.RecoveredRecords.Add(record);
		context.Confirmation.Result = true;
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();

		await viewModel.DeleteCommand.ExecuteAsync();

		Assert.Equal(1, context.Confirmation.Calls);
		Assert.Equal(1, context.Storage.DiscardCalls);
		Assert.False(File.Exists(record.LocalPath));
		Assert.Empty(viewModel.Records);
	}

	[Fact]
	public async Task ManualDelete_UserDeclines_RetainsLocalFileAndRecord()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		record.VerificationState = VerificationState.Verified;
		context.Storage.RecoveredRecords.Add(record);
		context.Confirmation.Result = false;
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();

		await viewModel.DeleteCommand.ExecuteAsync();

		Assert.Equal(1, context.Confirmation.Calls);
		Assert.Equal(0, context.Storage.DiscardCalls);
		Assert.True(File.Exists(record.LocalPath));
		Assert.Contains(record, viewModel.Records);
		Assert.Equal("Local deletion cancelled.", viewModel.StatusMessage);
	}

	[Fact]
	public async Task RecordCommand_WhileAlreadyExecuting_DoesNotOpenSecondCamera()
	{
		using var context = new ViewModelContext();
		var started = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<VideoRecordingResult?>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		context.Recording.Handler = (_, _) =>
		{
			started.SetResult();
			return release.Task;
		};
		var viewModel = context.CreateViewModel();

		var first = viewModel.RecordCommand.ExecuteAsync();
		await started.Task;
		await viewModel.RecordCommand.ExecuteAsync();
		release.SetResult(null);
		await first;

		Assert.Equal(1, context.Recording.Calls);
	}

	[Fact]
	public async Task UploadAsync_ReportsProgressAndUpdatesStatus()
	{
		using var context = new ViewModelContext();
		var record = context.CreateLocalRecord();
		context.Storage.RecoveredRecords.Add(record);
		var release = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		context.Transfer.Handler = async (candidate, progress, _) =>
		{
			candidate.UploadProgress = 0.5;
			progress?.Report(new TransferProgress(
				candidate.FileSizeBytes / 2,
				candidate.FileSizeBytes));
			await release.Task;
			candidate.UploadState = UploadState.Uploaded;
		};
		context.Verification.Handler = (candidate, _) =>
		{
			candidate.VerificationState = VerificationState.Failed;
			return Task.FromResult(new VerificationResult(false, null, "test stop"));
		};
		var viewModel = context.CreateViewModel();
		await viewModel.InitializeAsync();
		var progressReported = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		viewModel.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(MainPageViewModel.StatusMessage) &&
			    viewModel.StatusMessage.StartsWith("Uploading", StringComparison.Ordinal))
				progressReported.TrySetResult();
		};

		await viewModel.UploadCommand.ExecuteAsync();
		await progressReported.Task;

		Assert.Equal(0.5, viewModel.UploadProgress);
		Assert.StartsWith("Uploading", viewModel.StatusMessage);
		release.SetResult();
		await viewModel.WaitForTransfersAsync();
	}

	private sealed class ViewModelContext : IDisposable
	{
		private readonly TemporaryDirectory _temp = new();

		public FakeVideoRecordingService Recording { get; } = new();
		public FakeVideoStorageService Storage { get; }
		public FakeVideoTransferService Transfer { get; } = new();
		public FakeTransferVerificationService Verification { get; } = new();
		public FakeVideoRecordRepository Repository { get; } = new();
		public FakeConfirmationService Confirmation { get; } = new();
		public FakeRemoteTransferSettings Settings { get; } = new();

		public ViewModelContext() => Storage = new FakeVideoStorageService(_temp.Path);

		public MainPageViewModel CreateViewModel() =>
			new(
				Recording,
				Storage,
				Transfer,
				Verification,
				Repository,
				Confirmation,
				Settings,
				new FakeAppVersionProvider("1.2.3", "42"));

		public VideoRecord CreateLocalRecord()
		{
			var path = _temp.CreateFile($"{Guid.NewGuid():N}.mov", new byte[12]);
			return new VideoRecord
			{
				FileName = Path.GetFileName(path),
				LocalPath = path,
				FileSizeBytes = 12,
				Duration = TimeSpan.FromMinutes(30),
				RecordingState = RecordingState.Recorded
			};
		}

		public void Dispose() => _temp.Dispose();
	}
}
