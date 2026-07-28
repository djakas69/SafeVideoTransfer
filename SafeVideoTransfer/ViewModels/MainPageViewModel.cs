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
	private readonly IPhotoLibraryService _photos;
	private readonly IVideoRecordRepository _repository;
	private readonly RemoteTransferSettings _settings;
	private CancellationTokenSource? _operationCts;
	private VideoRecord? _current;
	private string _statusMessage = "Ready";
	private bool _isBusy;
	private string _remoteUrl;
	private string _username;
	private string _password;
	private bool _verifyByDownloading;
	private bool _isRecording;

	public MainPageViewModel(
		IVideoRecordingService recording,
		IVideoStorageService storage,
		IVideoTransferService transfer,
		ITransferVerificationService verification,
		IPhotoLibraryService photos,
		IVideoRecordRepository repository,
		RemoteTransferSettings settings)
	{
		_recording = recording;
		_storage = storage;
		_transfer = transfer;
		_verification = verification;
		_photos = photos;
		_repository = repository;
		_settings = settings;
		_remoteUrl = settings.BaseUrl;
		_username = settings.Username;
		_password = settings.Password;
		_verifyByDownloading = settings.VerifyByDownloading;

		RecordCommand = MakeCommand(RecordAsync, () => !IsBusy);
		UploadCommand = MakeCommand(UploadAndVerifyAsync,
			() => !IsBusy && Current?.LocalFileExists == true);
		CancelCommand = new AsyncCommand(CancelAsync, () => IsBusy);
		DeleteCommand = MakeCommand(DeleteAsync,
			() => !IsBusy && Current?.CanDelete == true && Current.KeepLocal == false);
		KeepCommand = MakeCommand(KeepAsync,
			() => !IsBusy && Current?.LocalFileExists == true);
		SaveToPhotosCommand = MakeCommand(SaveToPhotosAsync,
			() => !IsBusy && Current?.LocalFileExists == true);
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

	public string FileName => Current?.FileName ?? "—";
	public string AppVersion =>
		$"Version {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";
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
	public AsyncCommand KeepCommand { get; }
	public AsyncCommand SaveToPhotosCommand { get; }

	public async Task InitializeAsync()
	{
		if (Records.Count > 0) return;
		await _settings.LoadSecretsAsync();
		_password = _settings.Password;
		OnPropertyChanged(nameof(Password));
		foreach (var record in await _storage.RecoverAsync(CancellationToken.None))
			Records.Add(record);
		Current = Records.FirstOrDefault();
		StatusMessage = Records.Count == 0 ? "Ready to record." : "Recovered saved video records.";
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
			StatusMessage = "Saved in app-local storage. Configure the server, then upload.";
		});
	}

	private async Task UploadAndVerifyAsync()
	{
		var record = Current ?? throw new InvalidOperationException("Select a local video first.");
		await RunOperationAsync("Uploading…", async token =>
		{
			var progress = new Progress<TransferProgress>(_ =>
			{
				RefreshRecordProperties();
				StatusMessage = $"Uploading {UploadProgress:P0}";
			});
			await _transfer.UploadAsync(record, progress, token);
			RefreshRecordProperties();
			StatusMessage = "Upload finished. Verifying remote copy…";
			var result = await _verification.VerifyAsync(record, token);
			RefreshRecordProperties();
			StatusMessage = result.IsVerified
				? $"Verified: {result.Message} Choose Delete local or Keep local."
				: $"Verification failed: {result.Message} The local file was kept.";
		});
	}

	private Task CancelAsync()
	{
		_operationCts?.Cancel();
		StatusMessage = "Cancelling…";
		return Task.CompletedTask;
	}

	private async Task DeleteAsync()
	{
		var record = Current ?? throw new InvalidOperationException("Select a video first.");
		await RunOperationAsync("Deleting local file…", async token =>
		{
			await _storage.DeleteLocalAsync(record, token);
			RefreshRecordProperties();
			StatusMessage = "Local sandbox file deleted. It does not enter Photos or Recently Deleted.";
		});
	}

	private async Task KeepAsync()
	{
		var record = Current ?? throw new InvalidOperationException("Select a video first.");
		record.KeepLocal = true;
		record.DeletionState = DeletionState.Kept;
		await _repository.UpsertAsync(record, CancellationToken.None);
		RefreshRecordProperties();
		StatusMessage = "Local file marked Keep Local.";
	}

	private async Task SaveToPhotosAsync()
	{
		var record = Current ?? throw new InvalidOperationException("Select a video first.");
		await RunOperationAsync("Saving a copy to Photos…", async token =>
		{
			await _photos.SaveCopyAsync(record, token);
			StatusMessage = "A separate copy was saved to Photos.";
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
		KeepCommand?.RaiseCanExecuteChanged();
		SaveToPhotosCommand?.RaiseCanExecuteChanged();
	}

	private static string FormatBytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var size = (double)bytes;
		var unit = 0;
		while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
		return $"{size:0.##} {units[unit]}";
	}
}
