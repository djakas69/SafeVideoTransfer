using System.Collections.Concurrent;
using System.Security.Cryptography;
using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;

namespace SafeVideoTransfer.Tests.TestSupport;

internal sealed class FakeAppDataDirectoryProvider(string path) : IAppDataDirectoryProvider
{
	public string AppDataDirectory { get; } = path;
}

internal sealed class FixedTimeProvider(
	DateTimeOffset utcNow,
	TimeZoneInfo? timeZone = null) : TimeProvider
{
	public override DateTimeOffset GetUtcNow() => utcNow;
	public override TimeZoneInfo LocalTimeZone { get; } = timeZone ?? TimeZoneInfo.Utc;
}

internal sealed class FakeAppVersionProvider(
	string version = "test",
	string build = "1") : IAppVersionProvider
{
	public string VersionString { get; } = version;
	public string BuildString { get; } = build;
}

internal sealed class FakeRemoteTransferSettings : IRemoteTransferSettings
{
	public string BaseUrl { get; set; } = "ftp://test-host/videos/";
	public string Username { get; set; } = "user";
	public string Password { get; set; } = "password";
	public bool VerifyByDownloading { get; set; } = true;
	public int LoadSecretsCalls { get; private set; }

	public Task LoadSecretsAsync()
	{
		LoadSecretsCalls++;
		return Task.CompletedTask;
	}

	public FtpTarget GetFtpTarget(string fileName)
	{
		var baseUri = new Uri(BaseUrl);
		var remotePath = $"{baseUri.AbsolutePath.TrimEnd('/')}/{fileName}";
		return new FtpTarget(
			baseUri.Host,
			baseUri.IsDefaultPort ? 21 : baseUri.Port,
			remotePath,
			new Uri(baseUri, Uri.EscapeDataString(fileName)));
	}
}

internal sealed class FakeVideoRecordRepository : IVideoRecordRepository
{
	private readonly object _gate = new();
	private readonly List<VideoRecord> _records;

	public FakeVideoRecordRepository(IEnumerable<VideoRecord>? records = null) =>
		_records = records?.ToList() ?? [];

	public Exception? UpsertException { get; set; }
	public int UpsertCalls { get; private set; }

	public Task<IReadOnlyList<VideoRecord>> GetAllAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
			return Task.FromResult<IReadOnlyList<VideoRecord>>(_records.ToList());
	}

	public Task UpsertAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		UpsertCalls++;
		if (UpsertException is not null) throw UpsertException;
		lock (_gate)
		{
			var index = _records.FindIndex(candidate => candidate.Id == record.Id);
			if (index >= 0) _records[index] = record;
			else _records.Add(record);
		}
		return Task.CompletedTask;
	}
}

internal sealed class FakeVideoRecordingService : IVideoRecordingService
{
	public int Calls { get; private set; }
	public Func<string, CancellationToken, Task<VideoRecordingResult?>> Handler { get; set; } =
		(_, _) => Task.FromResult<VideoRecordingResult?>(
			new VideoRecordingResult(TimeSpan.FromMinutes(1)));

	public Task<VideoRecordingResult?> RecordAsync(
		string destinationPath, CancellationToken cancellationToken)
	{
		Calls++;
		return Handler(destinationPath, cancellationToken);
	}
}

internal sealed class FakeVideoStorageService : IVideoStorageService
{
	private readonly string _root;

	public FakeVideoStorageService(string root) => _root = root;

	public List<VideoRecord> RecoveredRecords { get; } = [];
	public VideoRecord? RegisteredRecord { get; set; }
	public int DeleteCalls { get; private set; }
	public int DiscardCalls { get; private set; }
	public Func<VideoRecord, CancellationToken, Task>? DeleteHandler { get; set; }
	public Func<VideoRecord, CancellationToken, Task>? DiscardHandler { get; set; }
	public string ComputedHash { get; set; } = string.Empty;

	public string CreateSafeVideoPath() =>
		System.IO.Path.Combine(_root, $".pending-{Guid.NewGuid():N}.mov");

	public Task<VideoRecord> RegisterRecordingAsync(
		string path, TimeSpan duration, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var record = RegisteredRecord ?? new VideoRecord
		{
			FileName = "2026-07-29-1.mov",
			LocalPath = path,
			FileSizeBytes = File.Exists(path) ? new FileInfo(path).Length : 4,
			Duration = duration,
			RecordingState = RecordingState.Recorded
		};
		RegisteredRecord = record;
		return Task.FromResult(record);
	}

	public async Task DeleteLocalAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		DeleteCalls++;
		if (DeleteHandler is not null)
		{
			await DeleteHandler(record, cancellationToken);
			return;
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (record.VerificationState != VerificationState.Verified)
			throw new InvalidOperationException("Not verified.");
		if (File.Exists(record.LocalPath)) File.Delete(record.LocalPath);
		record.DeletionState = DeletionState.Deleted;
	}

	public async Task DiscardLocalAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		DiscardCalls++;
		if (DiscardHandler is not null)
		{
			await DiscardHandler(record, cancellationToken);
			return;
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (File.Exists(record.LocalPath)) File.Delete(record.LocalPath);
		record.DeletionState = DeletionState.Deleted;
	}

	public Task<IReadOnlyList<VideoRecord>> RecoverAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult<IReadOnlyList<VideoRecord>>(RecoveredRecords.ToList());
	}

	public Task<string> ComputeSha256Async(
		VideoRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		record.Sha256 = ComputedHash;
		return Task.FromResult(ComputedHash);
	}
}

internal sealed class FakeVideoTransferService : IVideoTransferService
{
	public int Calls { get; private set; }
	public Func<VideoRecord, IProgress<TransferProgress>?, CancellationToken, Task> Handler { get; set; } =
		(record, progress, _) =>
		{
			record.UploadState = UploadState.Uploaded;
			record.UploadProgress = 1;
			progress?.Report(new TransferProgress(record.FileSizeBytes, record.FileSizeBytes));
			return Task.CompletedTask;
		};

	public Task UploadAsync(
		VideoRecord record,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		Calls++;
		return Handler(record, progress, cancellationToken);
	}
}

internal sealed class FakeTransferVerificationService : ITransferVerificationService
{
	public int Calls { get; private set; }
	public Func<VideoRecord, CancellationToken, Task<VerificationResult>> Handler { get; set; } =
		(record, _) =>
		{
			record.VerificationState = VerificationState.Verified;
			return Task.FromResult(new VerificationResult(
				true, record.FileSizeBytes, "Verified."));
		};

	public Task<VerificationResult> VerifyAsync(
		VideoRecord record, CancellationToken cancellationToken)
	{
		Calls++;
		return Handler(record, cancellationToken);
	}
}

internal sealed class FakeConfirmationService(bool result = true) : IUserConfirmationService
{
	public bool Result { get; set; } = result;
	public int Calls { get; private set; }

	public Task<bool> ConfirmAsync(
		string title, string message, string accept, string cancel)
	{
		Calls++;
		return Task.FromResult(Result);
	}
}

internal sealed class RecordingProgress : IProgress<TransferProgress>
{
	public List<TransferProgress> Values { get; } = [];
	public void Report(TransferProgress value) => Values.Add(value);
}

internal sealed class FakeAsyncDelay : IAsyncDelay
{
	public int Calls { get; private set; }
	public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
	{
		Calls++;
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}
}

internal sealed class FakeFtpClientFactory(Func<int, FakeFtpClient>? create = null) : IFtpClientFactory
{
	private readonly Func<int, FakeFtpClient> _create = create ?? (_ => new FakeFtpClient());
	public List<FakeFtpClient> Clients { get; } = [];

	public IFtpClient Create(FtpTarget target)
	{
		var client = _create(Clients.Count);
		Clients.Add(client);
		return client;
	}
}

internal sealed class FakeFtpClient : IFtpClient
{
	public long RemoteSize { get; set; } = -1;
	public bool UploadResult { get; set; } = true;
	public byte[] RemoteContent { get; set; } = [];
	public Exception? ConnectException { get; set; }
	public Exception? UploadException { get; set; }
	public int ConnectCalls { get; private set; }
	public int UploadCalls { get; private set; }
	public bool ResumeRequested { get; private set; }
	public bool Disposed { get; private set; }
	public TransferProgress? ProgressToReport { get; set; }

	public Task ConnectAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ConnectCalls++;
		if (ConnectException is not null) throw ConnectException;
		return Task.CompletedTask;
	}

	public Task<long> GetFileSizeAsync(
		string remotePath, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(RemoteSize);
	}

	public Task<bool> UploadFileAsync(
		string localPath,
		string remotePath,
		bool resume,
		IProgress<TransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		UploadCalls++;
		ResumeRequested = resume;
		if (UploadException is not null) throw UploadException;
		if (ProgressToReport is { } value) progress?.Report(value);
		return Task.FromResult(UploadResult);
	}

	public Task<Stream> OpenReadAsync(
		string remotePath, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult<Stream>(new MemoryStream(RemoteContent, writable: false));
	}

	public void Dispose() => Disposed = true;
}

internal static class Hashes
{
	public static string Sha256(byte[] content) =>
		Convert.ToHexStringLower(SHA256.HashData(content));
}
