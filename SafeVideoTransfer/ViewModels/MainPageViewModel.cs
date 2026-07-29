using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;

namespace SafeVideoTransfer.ViewModels;

public sealed class MainPageViewModel : ObservableObject
{
	private readonly IVideoRecordingService _recording;
	private readonly IVideoStorageService _storage;
	private readonly IVideoTransferService _transfer;
	private readonly ITransferVerificationService _verification;
	private readonly IVideoRecordRepository _repository;
	private readonly IUserConfirmationService _confirmation;
	private readonly IRemoteTransferSettings _settings;
	private readonly IAppVersionProvider _appVersion;
	private readonly ConcurrentQueue<VideoRecord> _transferQueue = new();
	private readonly ConcurrentDictionary<Guid, byte> _scheduledTransfers = new();
	private readonly object _transferStateGate = new();
	private CancellationTokenSource? _operationCts;
	private CancellationTokenSource? _activeTransferCts;
	private VideoRecord? _current;
	private string _statusMessage = "Ready";
	private bool _isBusy;
	private string _remoteUrl;
	private string _username;
	private string _password;
	private bool _verifyByDownloading;
	private bool _isRecording;
	private bool _showFtpSettings;
	private bool _showRecoveredVideos;
	private bool _isTransferActive;
	private int _transferWorkerRunning;
	private Task? _initializationTask;
	private TaskCompletionSource _transfersIdle = CompletedTaskSource();

	public MainPageViewModel(
		IVideoRecordingService recording,
		IVideoStorageService storage,
		IVideoTransferService transfer,
		ITransferVerificationService verification,
		IVideoRecordRepository repository,
		IUserConfirmationService confirmation,
		IRemoteTransferSettings settings,
		IAppVersionProvider appVersion)
	{
		_recording = recording;
		_storage = storage;
		_transfer = transfer;
		_verification = verification;
		_repository = repository;
		_confirmation = confirmation;
		_settings = settings;
		_appVersion = appVersion;
		_remoteUrl = settings.BaseUrl;
		_username = settings.Username;
		_password = settings.Password;
		_verifyByDownloading = settings.VerifyByDownloading;

		RecordCommand = MakeCommand(RecordAsync, () => !IsBusy);
		UploadCommand = MakeCommand(UploadAndVerifyAsync,
			() => !IsBusy &&
			      Current?.LocalFileExists == true &&
			      !_scheduledTransfers.ContainsKey(Current.Id));
		CancelCommand = new AsyncCommand(CancelAsync, () => IsBusy || IsTransferActive);
		DeleteCommand = MakeCommand(DeleteAsync,
			() => !IsBusy &&
			      Current is not null &&
			      !_scheduledTransfers.ContainsKey(Current.Id));
		ToggleFtpSettingsCommand = MakeCommand(ToggleFtpSettingsAsync, () => !IsBusy);
		ToggleRecoveredVideosCommand = MakeCommand(ToggleRecoveredVideosAsync, () => !IsBusy);
		Records.CollectionChanged += (_, _) =>
			OnPropertyChanged(nameof(RecoveredVideosHeaderText));
	}

	public ObservableCollection<VideoRecord> Records { get; } = [];

	public VideoRecord? Current
	{
		get => _current;
		set
		{
			if (SetProperty(ref _current, value))
			{
				RefreshRecordProperties();
				RaiseCommands();
			}
		}
	}

	public string StatusMessage
	{
		get => _statusMessage;
		private set => SetProperty(ref _statusMessage, value);
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (SetProperty(ref _isBusy, value)) RaiseCommands();
		}
	}

	public bool IsTransferActive
	{
		get => _isTransferActive;
		private set
		{
			if (SetProperty(ref _isTransferActive, value)) RaiseCommands();
		}
	}

	public string RemoteUrl
	{
		get => _remoteUrl;
		set { if (SetProperty(ref _remoteUrl, value)) _settings.BaseUrl = value; }
	}

	public string Username
	{
		get => _username;
		set { if (SetProperty(ref _username, value)) _settings.Username = value; }
	}

	public string Password
	{
		get => _password;
		set { if (SetProperty(ref _password, value)) _settings.Password = value; }
	}

	public bool VerifyByDownloading
	{
		get => _verifyByDownloading;
		set { if (SetProperty(ref _verifyByDownloading, value)) _settings.VerifyByDownloading = value; }
	}

	public bool ShowFtpSettings
	{
		get => _showFtpSettings;
		private set
		{
			if (SetProperty(ref _showFtpSettings, value))
				OnPropertyChanged(nameof(FtpSettingsButtonText));
		}
	}

	public string FtpSettingsButtonText => ShowFtpSettings ? "Hide FTP settings ▲" : "FTP settings ▼";

	public bool ShowRecoveredVideos
	{
		get => _showRecoveredVideos;
		private set
		{
			if (SetProperty(ref _showRecoveredVideos, value))
			{
				OnPropertyChanged(nameof(RecoveredVideosHeaderText));
				OnPropertyChanged(nameof(VisibleRecords));
			}
		}
	}

	// A hidden iOS UICollectionView can reject ObservableCollection updates.
	// Detach its ItemsSource while collapsed and reconnect the completed snapshot
	// when the user expands the section.
	public IEnumerable<VideoRecord>? VisibleRecords =>
		ShowRecoveredVideos ? Records : null;

	public string RecoveredVideosHeaderText =>
		$"Recovered videos ({Records.Count}) {(ShowRecoveredVideos ? "▲" : "▼")}";
	public string FileName => Current?.FileName ?? "—";
	public string AppVersion =>
		$"Version {_appVersion.VersionString} (build {_appVersion.BuildString})";
	public string Duration => Current is null ? "—" : Current.Duration.ToString(@"hh\:mm\:ss");
	public string FileSize => Current is null ? "—" : FormatBytes(Current.FileSizeBytes);
	public string RecordingStatus =>
		_isRecording ? "Recording" : Current?.RecordingState.ToString() ?? "Ready";
	public string UploadStatus => Current?.UploadState.ToString() ?? "Not started";
	public string VerificationStatus => Current?.VerificationState.ToString() ?? "Not started";
	public string DeletionStatus => Current?.DeletionState.ToString() ?? "Not requested";
	public double UploadProgress => Current?.UploadProgress ?? 0;

	public AsyncCommand RecordCommand { get; }
	public AsyncCommand UploadCommand { get; }
	public AsyncCommand CancelCommand { get; }
	public AsyncCommand DeleteCommand { get; }
	public AsyncCommand ToggleFtpSettingsCommand { get; }
	public AsyncCommand ToggleRecoveredVideosCommand { get; }

	public async Task WaitForTransfersAsync()
	{
		while (true)
		{
			Task idleTask;
			lock (_transferStateGate)
				idleTask = _transfersIdle.Task;
			await idleTask;
			if (_scheduledTransfers.IsEmpty && !IsTransferActive)
				return;
		}
	}

	public Task InitializeAsync() =>
		_initializationTask ??= InitializeCoreAsync();

	private async Task InitializeCoreAsync()
	{
		try
		{
			await _settings.LoadSecretsAsync();
			_password = _settings.Password;
			OnPropertyChanged(nameof(Password));
			foreach (var record in await _storage.RecoverAsync(CancellationToken.None))
			{
				if (record.DeletionState != DeletionState.Deleted)
					Records.Add(record);
			}
			Current = Records.FirstOrDefault();
			StatusMessage = Records.Count == 0 ? "Ready to record." : "Recovered saved video records.";
		}
		catch (Exception ex)
		{
			StatusMessage = $"Startup recovery error: {ex.Message}";
		}
	}

	private async Task RecordAsync()
	{
		await RunOperationAsync("Opening camera…", async token =>
		{
			var path = _storage.CreateSafeVideoPath();
			_isRecording = true;
			RefreshRecordProperties();
			VideoRecordingResult? result;
			try
			{
				result = await _recording.RecordAsync(path, token);
			}
			finally
			{
				_isRecording = false;
				RefreshRecordProperties();
			}
			if (result is null)
			{
				StatusMessage = "Recording cancelled.";
				return;
			}
			var record = await _storage.RegisterRecordingAsync(path, result.Duration, token);
			Records.Insert(0, record);
			Current = record;
			QueueTransfer(record);
		});
	}

	private Task UploadAndVerifyAsync()
	{
		var record = Current ?? throw new InvalidOperationException("Select a local video first.");
		QueueTransfer(record);
		return Task.CompletedTask;
	}

	private void QueueTransfer(VideoRecord record)
	{
		if (!_scheduledTransfers.TryAdd(record.Id, 0))
		{
			StatusMessage = $"{record.FileName} is already queued or uploading.";
			return;
		}

		lock (_transferStateGate)
		{
			if (_scheduledTransfers.Count == 1)
				_transfersIdle = new TaskCompletionSource(
					TaskCreationOptions.RunContinuationsAsynchronously);
		}
		_transferQueue.Enqueue(record);
		StatusMessage = IsTransferActive
			? $"{record.FileName} queued for FTP transfer."
			: $"{record.FileName} saved locally. Starting FTP transfer…";
		RaiseCommands();
		StartTransferWorker();
	}

	private void StartTransferWorker()
	{
		if (Interlocked.CompareExchange(ref _transferWorkerRunning, 1, 0) == 0)
			_ = DrainTransferQueueAsync();
	}

	private async Task DrainTransferQueueAsync()
	{
		while (_transferQueue.TryDequeue(out var record))
		{
			_activeTransferCts?.Dispose();
			_activeTransferCts = new CancellationTokenSource();
			IsTransferActive = true;
			try
			{
				await TransferVerifyAndDeleteAsync(record, _activeTransferCts.Token);
			}
			catch (OperationCanceledException)
			{
				StatusMessage = $"{record.FileName} transfer interrupted. The local file was kept.";
			}
			catch (Exception ex)
			{
				record.LastError = ex.Message;
				await _repository.UpsertAsync(record, CancellationToken.None);
				StatusMessage = $"{record.FileName} transfer error: {ex.Message}";
			}
			finally
			{
				_activeTransferCts.Dispose();
				_activeTransferCts = null;
				_scheduledTransfers.TryRemove(record.Id, out _);
				RaiseCommands();
			}
		}

		IsTransferActive = false;
		Interlocked.Exchange(ref _transferWorkerRunning, 0);

		// Close the small race where a record is queued as this worker exits.
		if (!_transferQueue.IsEmpty)
			StartTransferWorker();
		else
		{
			lock (_transferStateGate)
				_transfersIdle.TrySetResult();
		}
	}

	private async Task TransferVerifyAndDeleteAsync(
		VideoRecord record, CancellationToken token)
	{
		var progress = new CallbackProgress<TransferProgress>(_ =>
		{
			RefreshRecordProperties();
			StatusMessage = $"Uploading {record.FileName}: {record.UploadProgress:P0}";
		});
		await _transfer.UploadAsync(record, progress, token);
		RefreshRecordProperties();
		StatusMessage = "Upload finished. Verifying remote copy…";
		var result = await _verification.VerifyAsync(record, token);
		RefreshRecordProperties();
		if (!result.IsVerified)
		{
			StatusMessage = $"Verification failed: {result.Message} The local file was kept.";
			return;
		}

		StatusMessage = $"Verified: {result.Message} Deleting local file…";
		await _storage.DeleteLocalAsync(record, token);
		Records.Remove(record);
		if (ReferenceEquals(Current, record))
			Current = Records.FirstOrDefault();
		else
			RefreshRecordProperties();
		StatusMessage = "Upload verified. Local file deleted and removed from the list.";
	}

	private Task CancelAsync()
	{
		StatusMessage = "Cancelling…";
		_operationCts?.Cancel();
		_activeTransferCts?.Cancel();
		return Task.CompletedTask;
	}

	private Task ToggleFtpSettingsAsync()
	{
		ShowFtpSettings = !ShowFtpSettings;
		return Task.CompletedTask;
	}

	private Task ToggleRecoveredVideosAsync()
	{
		ShowRecoveredVideos = !ShowRecoveredVideos;
		return Task.CompletedTask;
	}

	private async Task DeleteAsync()
	{
		var record = Current ?? throw new InvalidOperationException("Select a video first.");
		var confirmed = await _confirmation.ConfirmAsync(
			"Delete local video?",
			$"Delete {record.FileName} permanently from this iPhone? This video will not go to Recently Deleted.",
			"Delete",
			"Cancel");
		if (!confirmed)
		{
			StatusMessage = "Local deletion cancelled.";
			return;
		}

		await RunOperationAsync("Deleting selected local video…", async token =>
		{
			await _storage.DiscardLocalAsync(record, token);
			Records.Remove(record);
			Current = Records.FirstOrDefault();
			StatusMessage = "Selected local video deleted and removed from the list.";
		});
	}

	private async Task RunOperationAsync(string initialStatus, Func<CancellationToken, Task> action)
	{
		_operationCts?.Dispose();
		_operationCts = new CancellationTokenSource();
		IsBusy = true;
		StatusMessage = initialStatus;
		try
		{
			await action(_operationCts.Token);
		}
		catch (OperationCanceledException)
		{
			StatusMessage = "Operation interrupted. The local file was kept and can be retried.";
		}
		catch (Exception ex)
		{
			StatusMessage = $"Error: {ex.Message}";
			if (Current is not null)
			{
				Current.LastError = ex.Message;
				await _repository.UpsertAsync(Current, CancellationToken.None);
			}
		}
		finally
		{
			IsBusy = false;
			RefreshRecordProperties();
		}
	}

	private AsyncCommand MakeCommand(Func<Task> action, Func<bool> canExecute) =>
		new(action, canExecute, ex => StatusMessage = $"Error: {ex.Message}");

	private void RefreshRecordProperties()
	{
		OnPropertyChanged(nameof(FileName));
		OnPropertyChanged(nameof(Duration));
		OnPropertyChanged(nameof(FileSize));
		OnPropertyChanged(nameof(RecordingStatus));
		OnPropertyChanged(nameof(UploadStatus));
		OnPropertyChanged(nameof(VerificationStatus));
		OnPropertyChanged(nameof(DeletionStatus));
		OnPropertyChanged(nameof(UploadProgress));
		RaiseCommands();
	}

	private void RaiseCommands()
	{
		RecordCommand?.RaiseCanExecuteChanged();
		UploadCommand?.RaiseCanExecuteChanged();
		CancelCommand?.RaiseCanExecuteChanged();
		DeleteCommand?.RaiseCanExecuteChanged();
		ToggleFtpSettingsCommand?.RaiseCanExecuteChanged();
		ToggleRecoveredVideosCommand?.RaiseCanExecuteChanged();
	}

	private static string FormatBytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var size = (double)bytes;
		var unit = 0;
		while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
		return $"{size:0.##} {units[unit]}";
	}

	private static TaskCompletionSource CompletedTaskSource()
	{
		var source = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		source.SetResult();
		return source;
	}

	private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
	{
		public void Report(T value) => callback(value);
	}
}
