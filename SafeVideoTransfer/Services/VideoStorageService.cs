using System.Security.Cryptography;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class VideoStorageService(
	IVideoRecordRepository repository,
	IAppDataDirectoryProvider appData,
	TimeProvider timeProvider) : IVideoStorageService
{
	private readonly string _videoDirectory = Path.Combine(appData.AppDataDirectory, "Videos");
	private readonly SemaphoreSlim _nameGate = new(1, 1);

	public string CreateSafeVideoPath()
	{
		Directory.CreateDirectory(_videoDirectory);
		return Path.Combine(_videoDirectory, $".pending-{Guid.NewGuid():N}.mov");
	}

	public async Task<VideoRecord> RegisterRecordingAsync(
		string path, TimeSpan duration, CancellationToken cancellationToken)
	{
		var pendingFile = new FileInfo(path);
		if (!pendingFile.Exists || pendingFile.Length == 0)
			throw new InvalidOperationException("The camera did not produce a video file.");

		await _nameGate.WaitAsync(cancellationToken);
		try
		{
			var records = await repository.GetAllAsync(cancellationToken);
			var destinationPath = GetNextDailyPath(records);
			File.Move(pendingFile.FullName, destinationPath);
			var file = new FileInfo(destinationPath);
			var record = new VideoRecord
			{
				FileName = file.Name,
				LocalPath = file.FullName,
				FileSizeBytes = file.Length,
				Duration = duration,
				RecordingState = RecordingState.Recorded
			};
			await repository.UpsertAsync(record, cancellationToken);
			return record;
		}
		finally
		{
			_nameGate.Release();
		}
	}

	public async Task DeleteLocalAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (record.VerificationState != VerificationState.Verified)
			throw new InvalidOperationException("A local video cannot be deleted before remote verification succeeds.");

		await DeleteFileAndUpdateRecordAsync(record, cancellationToken);
	}

	public Task DiscardLocalAsync(VideoRecord record, CancellationToken cancellationToken) =>
		DeleteFileAndUpdateRecordAsync(record, cancellationToken);

	private async Task DeleteFileAndUpdateRecordAsync(
		VideoRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			if (File.Exists(record.LocalPath)) File.Delete(record.LocalPath);
			record.DeletionState = DeletionState.Deleted;
			await repository.UpsertAsync(record, CancellationToken.None);
		}
		catch
		{
			record.DeletionState = DeletionState.Failed;
			await repository.UpsertAsync(record, CancellationToken.None);
			throw;
		}
	}

	public async Task<IReadOnlyList<VideoRecord>> RecoverAsync(CancellationToken cancellationToken)
	{
		var records = (await repository.GetAllAsync(cancellationToken)).ToList();
		Directory.CreateDirectory(_videoDirectory);
		var indexedPaths = records.Select(x => x.LocalPath).ToHashSet(StringComparer.Ordinal);
		foreach (var discoveredPath in Directory.EnumerateFiles(_videoDirectory, "*.mov").ToList())
		{
			var path = discoveredPath;
			if (indexedPaths.Contains(path)) continue;

			if (Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal))
			{
				await _nameGate.WaitAsync(cancellationToken);
				try
				{
					var renamedPath = GetNextDailyPath(records);
					File.Move(path, renamedPath);
					path = renamedPath;
				}
				finally
				{
					_nameGate.Release();
				}
			}

			var file = new FileInfo(path);
			var recovered = new VideoRecord
			{
				FileName = file.Name,
				LocalPath = file.FullName,
				FileSizeBytes = file.Length,
				RecordingState = RecordingState.Recorded,
				LastError = "Recovered an unindexed local video after an interrupted app session."
			};
			await repository.UpsertAsync(recovered, cancellationToken);
			records.Add(recovered);
		}
		foreach (var record in records)
		{
			if (record.UploadState == UploadState.Uploading)
			{
				record.UploadState = UploadState.Interrupted;
				record.LastError = "Upload was interrupted when the app stopped.";
				await repository.UpsertAsync(record, cancellationToken);
			}
			if (record.VerificationState == VerificationState.Verifying)
			{
				record.VerificationState = VerificationState.NotStarted;
				record.LastError = "Verification was interrupted and can be retried.";
				await repository.UpsertAsync(record, cancellationToken);
			}
			if (!record.LocalFileExists && record.DeletionState != DeletionState.Deleted)
			{
				record.LastError = "The indexed local file is missing.";
				await repository.UpsertAsync(record, cancellationToken);
			}
		}
		return records.OrderByDescending(x => x.CreatedAtUtc).ToList();
	}

	public async Task<string> ComputeSha256Async(VideoRecord record, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(record.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
			1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var hash = await SHA256.HashDataAsync(stream, cancellationToken);
		record.Sha256 = Convert.ToHexStringLower(hash);
		await repository.UpsertAsync(record, cancellationToken);
		return record.Sha256;
	}

	private static int ParseDailyCounter(string fileName, string datePrefix)
	{
		var expectedPrefix = datePrefix + "-";
		if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
		    !fileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
			return 0;

		var counterText = fileName[expectedPrefix.Length..^4];
		return int.TryParse(counterText, out var counter) && counter > 0 ? counter : 0;
	}

	private string GetNextDailyPath(IEnumerable<VideoRecord> records)
	{
		var datePrefix = timeProvider.GetLocalNow().ToString("yyyy-MM-dd");
		var highestCounter = records
			.Select(record => ParseDailyCounter(record.FileName, datePrefix))
			.Concat(Directory
				.EnumerateFiles(_videoDirectory, $"{datePrefix}-*.mov")
				.Select(filePath => ParseDailyCounter(Path.GetFileName(filePath), datePrefix)))
			.DefaultIfEmpty(0)
			.Max();

		string destinationPath;
		do
		{
			highestCounter++;
			destinationPath = Path.Combine(
				_videoDirectory,
				$"{datePrefix}-{highestCounter}.mov");
		}
		while (File.Exists(destinationPath));

		return destinationPath;
	}
}
