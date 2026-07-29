using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;
using SafeVideoTransfer.Tests.TestSupport;
using TestFtpClient = SafeVideoTransfer.Tests.TestSupport.FakeFtpClient;

namespace SafeVideoTransfer.Tests;

public sealed class FtpVideoTransferServiceTests
{
	[Fact]
	public async Task UploadAsync_Success_MarksRecordUploaded()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp);
		var repository = new FakeVideoRecordRepository();
		var client = new TestFtpClient { RemoteSize = -1 };
		var service = CreateService(repository, new FakeFtpClientFactory(_ => client));

		await service.UploadAsync(record, null, CancellationToken.None);

		Assert.Equal(UploadState.Uploaded, record.UploadState);
		Assert.Equal(1, record.UploadProgress);
		Assert.Equal(1, client.UploadCalls);
		Assert.NotNull(record.RemoteUri);
	}

	[Fact]
	public async Task UploadAsync_ReportsIntermediateAndCompletedProgress()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp, [1, 2, 3, 4]);
		var client = new TestFtpClient
		{
			RemoteSize = -1,
			ProgressToReport = new TransferProgress(2, 4)
		};
		var progress = new RecordingProgress();
		var service = CreateService(
			new FakeVideoRecordRepository(),
			new FakeFtpClientFactory(_ => client));

		await service.UploadAsync(record, progress, CancellationToken.None);

		Assert.Contains(progress.Values, value => value.BytesSent == 2 && value.TotalBytes == 4);
		Assert.Contains(progress.Values, value => value.BytesSent == 4 && value.TotalBytes == 4);
	}

	[Fact]
	public async Task UploadAsync_ExactRemoteSize_SkipsDuplicateUploadAndMarksSuccess()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp, [1, 2, 3]);
		var client = new TestFtpClient { RemoteSize = 3 };
		var service = CreateService(
			new FakeVideoRecordRepository(),
			new FakeFtpClientFactory(_ => client));

		await service.UploadAsync(record, null, CancellationToken.None);

		Assert.Equal(0, client.UploadCalls);
		Assert.Equal(UploadState.Uploaded, record.UploadState);
	}

	[Fact]
	public async Task UploadAsync_PartialRemoteFile_RequestsResume()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp, [1, 2, 3, 4]);
		var client = new TestFtpClient { RemoteSize = 2 };
		var service = CreateService(
			new FakeVideoRecordRepository(),
			new FakeFtpClientFactory(_ => client));

		await service.UploadAsync(record, null, CancellationToken.None);

		Assert.True(client.ResumeRequested);
		Assert.Equal(UploadState.Uploaded, record.UploadState);
	}

	[Fact]
	public async Task UploadAsync_Cancelled_MarksInterruptedAndDoesNotMarkSuccess()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp);
		var client = new TestFtpClient
		{
			RemoteSize = -1,
			UploadException = new OperationCanceledException()
		};
		var service = CreateService(
			new FakeVideoRecordRepository(),
			new FakeFtpClientFactory(_ => client));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			service.UploadAsync(record, null, CancellationToken.None));

		Assert.Equal(UploadState.Interrupted, record.UploadState);
		Assert.NotEqual(UploadState.Uploaded, record.UploadState);
	}

	[Fact]
	public async Task UploadAsync_AuthenticationFailure_RetriesThenMarksFailed()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp);
		var delay = new FakeAsyncDelay();
		var factory = new FakeFtpClientFactory(_ => new TestFtpClient
		{
			ConnectException = new UnauthorizedAccessException("530 Login incorrect")
		});
		var service = CreateService(
			new FakeVideoRecordRepository(), factory, delay);

		var error = await Assert.ThrowsAsync<IOException>(() =>
			service.UploadAsync(record, null, CancellationToken.None));

		Assert.Contains("3 attempts", error.Message);
		Assert.Equal(3, factory.Clients.Count);
		Assert.Equal(2, delay.Calls);
		Assert.Equal(UploadState.Failed, record.UploadState);
		Assert.Contains("530", record.LastError);
	}

	[Fact]
	public async Task UploadAsync_IncompleteUploadResult_NeverMarksSuccessful()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateRecord(temp);
		var factory = new FakeFtpClientFactory(_ => new TestFtpClient
		{
			RemoteSize = -1,
			UploadResult = false
		});
		var service = CreateService(
			new FakeVideoRecordRepository(), factory, new FakeAsyncDelay());

		await Assert.ThrowsAsync<IOException>(() =>
			service.UploadAsync(record, null, CancellationToken.None));

		Assert.Equal(UploadState.Failed, record.UploadState);
		Assert.NotEqual(1, record.UploadProgress);
		Assert.Equal(3, factory.Clients.Sum(client => client.UploadCalls));
	}

	[Fact]
	public async Task UploadAsync_MissingLocalFile_FailsBeforeCreatingFtpClient()
	{
		using var temp = new TemporaryDirectory();
		var record = new VideoRecord
		{
			FileName = "missing.mov",
			LocalPath = Path.Combine(temp.Path, "missing.mov"),
			FileSizeBytes = 10
		};
		var factory = new FakeFtpClientFactory();
		var service = CreateService(new FakeVideoRecordRepository(), factory);

		await Assert.ThrowsAsync<FileNotFoundException>(() =>
			service.UploadAsync(record, null, CancellationToken.None));

		Assert.Empty(factory.Clients);
		Assert.NotEqual(UploadState.Uploaded, record.UploadState);
	}

	private static FtpVideoTransferService CreateService(
		IVideoRecordRepository repository,
		IFtpClientFactory factory,
		IAsyncDelay? delay = null) =>
		new(
			new FakeRemoteTransferSettings(),
			repository,
			factory,
			delay ?? new FakeAsyncDelay());

	private static VideoRecord CreateRecord(
		TemporaryDirectory temp, byte[]? content = null)
	{
		var bytes = content ?? [1, 2, 3, 4];
		var path = temp.CreateFile("video.mov", bytes);
		return new VideoRecord
		{
			FileName = "video.mov",
			LocalPath = path,
			FileSizeBytes = bytes.Length,
			RecordingState = RecordingState.Recorded
		};
	}
}
