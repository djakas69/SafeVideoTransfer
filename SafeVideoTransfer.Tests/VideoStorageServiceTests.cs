using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;
using SafeVideoTransfer.Tests.TestSupport;

namespace SafeVideoTransfer.Tests;

public sealed class VideoStorageServiceTests
{
	private static readonly DateTimeOffset FixedNow =
		new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

	[Fact]
	public void CreateSafeVideoPath_Twice_ReturnsUniqueAppLocalPendingPaths()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);

		var first = service.CreateSafeVideoPath();
		var second = service.CreateSafeVideoPath();

		Assert.NotEqual(first, second);
		Assert.StartsWith(Path.Combine(temp.Path, "Videos"), first);
		Assert.StartsWith(Path.Combine(temp.Path, "Videos"), second);
		Assert.StartsWith(".pending-", Path.GetFileName(first));
		Assert.EndsWith(".mov", first);
	}

	[Fact]
	public async Task RegisterRecordingAsync_FirstRecording_UsesDailyCounterOne()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var pending = service.CreateSafeVideoPath();
		File.WriteAllBytes(pending, [1, 2, 3]);

		var record = await service.RegisterRecordingAsync(
			pending, TimeSpan.FromMinutes(2), CancellationToken.None);

		Assert.Equal("2026-07-29-1.mov", record.FileName);
		Assert.Equal(Path.Combine(temp.Path, "Videos", record.FileName), record.LocalPath);
		Assert.True(File.Exists(record.LocalPath));
		Assert.False(File.Exists(pending));
		Assert.Equal(3, record.FileSizeBytes);
	}

	[Fact]
	public async Task RegisterRecordingAsync_ExistingDailyRecord_UsesNextCounter()
	{
		using var temp = new TemporaryDirectory();
		var existing = new VideoRecord { FileName = "2026-07-29-3.mov" };
		var repository = new FakeVideoRecordRepository([existing]);
		var service = CreateService(temp, repository);
		var pending = service.CreateSafeVideoPath();
		File.WriteAllBytes(pending, [1]);

		var record = await service.RegisterRecordingAsync(
			pending, TimeSpan.Zero, CancellationToken.None);

		Assert.Equal("2026-07-29-4.mov", record.FileName);
	}

	[Fact]
	public async Task RegisterRecordingAsync_ExistingUnindexedFile_AvoidsCollision()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var pending = service.CreateSafeVideoPath();
		File.WriteAllBytes(pending, [1]);
		File.WriteAllBytes(Path.Combine(temp.Path, "Videos", "2026-07-29-1.mov"), [8]);

		var record = await service.RegisterRecordingAsync(
			pending, TimeSpan.Zero, CancellationToken.None);

		Assert.Equal("2026-07-29-2.mov", record.FileName);
	}

	[Fact]
	public async Task RegisterRecordingAsync_EmptyFile_ThrowsAndRetainsPendingFile()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var pending = service.CreateSafeVideoPath();
		File.WriteAllBytes(pending, []);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.RegisterRecordingAsync(pending, TimeSpan.Zero, CancellationToken.None));

		Assert.True(File.Exists(pending));
	}

	[Fact]
	public async Task RegisterRecordingAsync_Cancelled_DoesNotMoveFile()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var pending = service.CreateSafeVideoPath();
		File.WriteAllBytes(pending, [1]);
		var token = new CancellationToken(canceled: true);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			service.RegisterRecordingAsync(pending, TimeSpan.Zero, token));

		Assert.True(File.Exists(pending));
	}

	[Fact]
	public async Task DeleteLocalAsync_VerifiedExistingFile_DeletesAndMarksRecord()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var path = temp.CreateFile("video.mov");
		var record = new VideoRecord
		{
			LocalPath = path,
			VerificationState = VerificationState.Verified
		};

		await service.DeleteLocalAsync(record, CancellationToken.None);

		Assert.False(File.Exists(path));
		Assert.Equal(DeletionState.Deleted, record.DeletionState);
	}

	[Fact]
	public async Task DeleteLocalAsync_UnverifiedFile_ThrowsAndRetainsFile()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var path = temp.CreateFile("video.mov");
		var record = new VideoRecord { LocalPath = path };

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.DeleteLocalAsync(record, CancellationToken.None));

		Assert.True(File.Exists(path));
		Assert.NotEqual(DeletionState.Deleted, record.DeletionState);
	}

	[Fact]
	public async Task DeleteLocalAsync_MissingVerifiedFile_CompletesGracefully()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var record = new VideoRecord
		{
			LocalPath = Path.Combine(temp.Path, "missing.mov"),
			VerificationState = VerificationState.Verified
		};

		await service.DeleteLocalAsync(record, CancellationToken.None);

		Assert.Equal(DeletionState.Deleted, record.DeletionState);
	}

	[Fact]
	public async Task DeleteLocalAsync_Cancelled_DoesNotDeleteFile()
	{
		using var temp = new TemporaryDirectory();
		var service = CreateService(temp);
		var path = temp.CreateFile("video.mov");
		var record = new VideoRecord
		{
			LocalPath = path,
			VerificationState = VerificationState.Verified
		};

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			service.DeleteLocalAsync(record, new CancellationToken(canceled: true)));

		Assert.True(File.Exists(path));
	}

	[Fact]
	public void CreateSafeVideoPath_VideosPathIsAFile_PropagatesIoError()
	{
		using var temp = new TemporaryDirectory();
		File.WriteAllText(Path.Combine(temp.Path, "Videos"), "not a directory");
		var service = CreateService(temp);

		Assert.ThrowsAny<IOException>(() => service.CreateSafeVideoPath());
	}

	private static VideoStorageService CreateService(
		TemporaryDirectory temp,
		IVideoRecordRepository? repository = null) =>
		new(
			repository ?? new FakeVideoRecordRepository(),
			new FakeAppDataDirectoryProvider(temp.Path),
			new FixedTimeProvider(FixedNow));
}
