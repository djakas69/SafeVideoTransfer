using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;
using SafeVideoTransfer.Tests.TestSupport;
using TestFtpClient = SafeVideoTransfer.Tests.TestSupport.FakeFtpClient;

namespace SafeVideoTransfer.Tests;

public sealed class FtpTransferVerificationServiceTests
{
	[Fact]
	public async Task VerifyAsync_SizeMatchesWithoutDownload_MarksVerified()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateUploadedRecord(temp, [1, 2, 3]);
		var settings = new FakeRemoteTransferSettings { VerifyByDownloading = false };
		var client = new TestFtpClient { RemoteSize = 3 };
		var service = CreateService(settings, client, new FakeVideoStorageService(temp.Path));

		var result = await service.VerifyAsync(record, CancellationToken.None);

		Assert.True(result.IsVerified);
		Assert.Equal(VerificationState.Verified, record.VerificationState);
		Assert.Equal(3, result.RemoteLength);
	}

	[Fact]
	public async Task VerifyAsync_RemoteSizeMismatch_MarksFailed()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateUploadedRecord(temp, [1, 2, 3]);
		var client = new TestFtpClient { RemoteSize = 2 };
		var service = CreateService(
			new FakeRemoteTransferSettings { VerifyByDownloading = false },
			client,
			new FakeVideoStorageService(temp.Path));

		var result = await service.VerifyAsync(record, CancellationToken.None);

		Assert.False(result.IsVerified);
		Assert.Equal(VerificationState.Failed, record.VerificationState);
		Assert.Contains("does not match", result.Message);
	}

	[Fact]
	public async Task VerifyAsync_HashMatches_MarksVerified()
	{
		using var temp = new TemporaryDirectory();
		byte[] content = [1, 3, 5, 7];
		var record = CreateUploadedRecord(temp, content);
		var storage = new FakeVideoStorageService(temp.Path)
		{
			ComputedHash = Hashes.Sha256(content)
		};
		var client = new TestFtpClient
		{
			RemoteSize = content.Length,
			RemoteContent = content
		};
		var service = CreateService(
			new FakeRemoteTransferSettings { VerifyByDownloading = true },
			client,
			storage);

		var result = await service.VerifyAsync(record, CancellationToken.None);

		Assert.True(result.IsVerified);
		Assert.Equal(VerificationState.Verified, record.VerificationState);
		Assert.Contains("SHA-256", result.Message);
	}

	[Fact]
	public async Task VerifyAsync_HashMismatch_MarksFailed()
	{
		using var temp = new TemporaryDirectory();
		byte[] local = [1, 2, 3];
		var record = CreateUploadedRecord(temp, local);
		var storage = new FakeVideoStorageService(temp.Path)
		{
			ComputedHash = Hashes.Sha256(local)
		};
		var client = new TestFtpClient
		{
			RemoteSize = local.Length,
			RemoteContent = [9, 9, 9]
		};
		var service = CreateService(
			new FakeRemoteTransferSettings { VerifyByDownloading = true },
			client,
			storage);

		var result = await service.VerifyAsync(record, CancellationToken.None);

		Assert.False(result.IsVerified);
		Assert.Equal(VerificationState.Failed, record.VerificationState);
		Assert.Contains("does not match", result.Message);
	}

	[Fact]
	public async Task VerifyAsync_UploadNotCompleted_ReturnsFailureWithoutNetworkCall()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateUploadedRecord(temp, [1]);
		record.UploadState = UploadState.Failed;
		var factory = new FakeFtpClientFactory();
		var service = new FtpTransferVerificationService(
			new FakeRemoteTransferSettings(),
			new FakeVideoStorageService(temp.Path),
			new FakeVideoRecordRepository(),
			factory);

		var result = await service.VerifyAsync(record, CancellationToken.None);

		Assert.False(result.IsVerified);
		Assert.Empty(factory.Clients);
	}

	[Fact]
	public async Task VerifyAsync_NetworkFailure_MarksFailed()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateUploadedRecord(temp, [1, 2]);
		var client = new TestFtpClient
		{
			ConnectException = new IOException("network unavailable")
		};
		var service = CreateService(
			new FakeRemoteTransferSettings(),
			client,
			new FakeVideoStorageService(temp.Path));

		var result = await service.VerifyAsync(record, CancellationToken.None);

		Assert.False(result.IsVerified);
		Assert.Equal(VerificationState.Failed, record.VerificationState);
		Assert.Contains("network unavailable", result.Message);
	}

	[Fact]
	public async Task VerifyAsync_Cancelled_PropagatesAndNeverMarksVerified()
	{
		using var temp = new TemporaryDirectory();
		var record = CreateUploadedRecord(temp, [1, 2]);
		var service = CreateService(
			new FakeRemoteTransferSettings(),
			new TestFtpClient(),
			new FakeVideoStorageService(temp.Path));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			service.VerifyAsync(record, new CancellationToken(canceled: true)));

		Assert.NotEqual(VerificationState.Verified, record.VerificationState);
	}

	private static FtpTransferVerificationService CreateService(
		IRemoteTransferSettings settings,
		TestFtpClient client,
		IVideoStorageService storage) =>
		new(
			settings,
			storage,
			new FakeVideoRecordRepository(),
			new FakeFtpClientFactory(_ => client));

	private static VideoRecord CreateUploadedRecord(
		TemporaryDirectory temp, byte[] content)
	{
		var path = temp.CreateFile("video.mov", content);
		return new VideoRecord
		{
			FileName = "video.mov",
			LocalPath = path,
			FileSizeBytes = content.Length,
			UploadState = UploadState.Uploaded,
			RemoteUri = "ftp://test-host/videos/video.mov"
		};
	}
}
