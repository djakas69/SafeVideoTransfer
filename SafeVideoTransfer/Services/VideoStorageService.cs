using System.Security.Cryptography;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class VideoStorageService(IVideoRecordRepository repository) : IVideoStorageService
{
	private readonly string _videoDirectory = Path.Combine(FileSystem.AppDataDirectory, "Videos");

	public string CreateSafeVideoPath()
	{
		Directory.CreateDirectory(_videoDirectory);
		var unique = Guid.NewGuid().ToString("N")[..12];
		return Path.Combine(_videoDirectory,
			$"video-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{unique}.mov");
	}

	public async Task<VideoRecord> RegisterRecordingAsync(
		string path, TimeSpan duration, CancellationToken cancellationToken)
	{
		var file = new FileInfo(path);
		if (!file.Exists || file.Length == 0)
			throw new InvalidOperationException("The camera did not produce a video file.");

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

	public async Task DeleteLocalAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (record.VerificationState != VerificationState.Verified)
			throw new InvalidOperationException("A local video cannot be deleted before remote verification succeeds.");
		if (record.KeepLocal)
			throw new InvalidOperationException("This video is marked Keep Local.");

		try
		{
			if (File.Exists(record.LocalPath)) File.Delete(record.LocalPath);
			record.DeletionState = DeletionState.Deleted;
			await repository.UpsertAsync(record, cancellationToken);
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
		foreach (var path in Directory.EnumerateFiles(_videoDirectory, "*.mov"))
		{
			if (indexedPaths.Contains(path)) continue;
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
}
