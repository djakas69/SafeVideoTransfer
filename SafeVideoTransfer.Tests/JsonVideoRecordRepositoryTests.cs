using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;
using SafeVideoTransfer.Tests.TestSupport;

namespace SafeVideoTransfer.Tests;

public sealed class JsonVideoRecordRepositoryTests
{
	private static readonly DateTimeOffset FixedNow =
		new(2026, 7, 29, 10, 11, 12, TimeSpan.Zero);

	[Fact]
	public async Task GetAllAsync_MissingRepositoryFile_ReturnsEmpty()
	{
		using var temp = new TemporaryDirectory();
		var repository = CreateRepository(temp);

		var records = await repository.GetAllAsync(CancellationToken.None);

		Assert.Empty(records);
	}

	[Fact]
	public async Task GetAllAsync_EmptyRepositoryFile_ReturnsEmptyAndQuarantinesFile()
	{
		using var temp = new TemporaryDirectory();
		var indexPath = temp.CreateFile("video-index.json", []);
		var repository = CreateRepository(temp);

		var records = await repository.GetAllAsync(CancellationToken.None);

		Assert.Empty(records);
		Assert.False(File.Exists(indexPath));
		Assert.True(File.Exists(indexPath + ".corrupt-20260729101112"));
	}

	[Fact]
	public async Task UpsertAsync_NewRecord_SavesAndLoadsRecord()
	{
		using var temp = new TemporaryDirectory();
		var repository = CreateRepository(temp);
		var record = new VideoRecord
		{
			FileName = "2026-07-29-1.mov",
			FileSizeBytes = 1234,
			Duration = TimeSpan.FromMinutes(30)
		};

		await repository.UpsertAsync(record, CancellationToken.None);
		var loaded = Assert.Single(await repository.GetAllAsync(CancellationToken.None));

		Assert.Equal(record.Id, loaded.Id);
		Assert.Equal(record.FileName, loaded.FileName);
		Assert.Equal(record.Duration, loaded.Duration);
	}

	[Fact]
	public async Task UpsertAsync_ExistingRecord_UpdatesWithoutDuplicate()
	{
		using var temp = new TemporaryDirectory();
		var repository = CreateRepository(temp);
		var record = new VideoRecord { FileName = "old.mov" };
		await repository.UpsertAsync(record, CancellationToken.None);
		record.FileName = "updated.mov";

		await repository.UpsertAsync(record, CancellationToken.None);
		var loaded = await repository.GetAllAsync(CancellationToken.None);

		Assert.Single(loaded);
		Assert.Equal("updated.mov", loaded[0].FileName);
	}

	[Fact]
	public async Task UpsertAsync_TransferState_PersistsAllSafetyStates()
	{
		using var temp = new TemporaryDirectory();
		var repository = CreateRepository(temp);
		var record = new VideoRecord
		{
			UploadState = UploadState.Uploaded,
			VerificationState = VerificationState.Verified,
			DeletionState = DeletionState.Deleted,
			UploadProgress = 1,
			RemoteUri = "ftp://test/videos/file.mov",
			Sha256 = "abc"
		};

		await repository.UpsertAsync(record, CancellationToken.None);
		var loaded = Assert.Single(await repository.GetAllAsync(CancellationToken.None));

		Assert.Equal(UploadState.Uploaded, loaded.UploadState);
		Assert.Equal(VerificationState.Verified, loaded.VerificationState);
		Assert.Equal(DeletionState.Deleted, loaded.DeletionState);
		Assert.Equal(1, loaded.UploadProgress);
		Assert.Equal(record.RemoteUri, loaded.RemoteUri);
		Assert.Equal(record.Sha256, loaded.Sha256);
	}

	[Fact]
	public async Task GetAllAsync_MalformedJson_QuarantinesFileAndReturnsEmpty()
	{
		using var temp = new TemporaryDirectory();
		var indexPath = temp.CreateFile(
			"video-index.json",
			"{not valid json"u8.ToArray());
		var repository = CreateRepository(temp);

		var loaded = await repository.GetAllAsync(CancellationToken.None);

		Assert.Empty(loaded);
		Assert.False(File.Exists(indexPath));
		Assert.True(File.Exists(indexPath + ".corrupt-20260729101112"));
	}

	[Fact]
	public async Task GetAllAsync_Cancelled_ThrowsOperationCanceled()
	{
		using var temp = new TemporaryDirectory();
		var repository = CreateRepository(temp);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			repository.GetAllAsync(new CancellationToken(canceled: true)));
	}

	private static JsonVideoRecordRepository CreateRepository(TemporaryDirectory temp) =>
		new(
			new FakeAppDataDirectoryProvider(temp.Path),
			new FixedTimeProvider(FixedNow));
}
