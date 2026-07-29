using System.Security.Cryptography;
using SafeVideoTransfer.Models;
using SafeVideoTransfer.Services;
using SafeVideoTransfer.IntegrationTests.Support;

namespace SafeVideoTransfer.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class LoopbackFtpIntegrationTests
{
	[Fact]
	public async Task UploadVerifyDelete_RealFluentFtpLoopback_RoundTripsContentSafely()
	{
		await using var server = new DisposableFtpServer();
		using var temp = new TemporaryDirectory();
		var pipeline = CreatePipeline(server, temp);
		var content = RandomNumberGenerator.GetBytes(256 * 1024);
		var record = await CreateRecordAsync(pipeline.Storage, content);

		await pipeline.Transfer.UploadAsync(record, null, CancellationToken.None);
		var verification = await pipeline.Verification.VerifyAsync(
			record, CancellationToken.None);

		Assert.True(verification.IsVerified);
		Assert.Equal(content, server.GetFile($"/uploads/{record.FileName}"));
		Assert.True(File.Exists(record.LocalPath));

		await pipeline.Storage.DeleteLocalAsync(record, CancellationToken.None);

		Assert.False(File.Exists(record.LocalPath));
		Assert.Equal(DeletionState.Deleted, record.DeletionState);
		var persisted = Assert.Single(
			await pipeline.Repository.GetAllAsync(CancellationToken.None));
		Assert.Equal(DeletionState.Deleted, persisted.DeletionState);
	}

	[Fact]
	public async Task UploadAsync_PartialRemoteFile_RealFluentFtpResumesAndCompletes()
	{
		await using var server = new DisposableFtpServer();
		using var temp = new TemporaryDirectory();
		var pipeline = CreatePipeline(server, temp);
		var content = RandomNumberGenerator.GetBytes(192 * 1024);
		var record = await CreateRecordAsync(pipeline.Storage, content);
		var partialLength = 64 * 1024;
		server.SetFile(
			$"/uploads/{record.FileName}",
			content[..partialLength]);

		await pipeline.Transfer.UploadAsync(record, null, CancellationToken.None);

		Assert.Equal(UploadState.Uploaded, record.UploadState);
		Assert.Equal(content, server.GetFile($"/uploads/{record.FileName}"));
		Assert.Contains(
			server.Commands,
			command => command.StartsWith("REST ", StringComparison.OrdinalIgnoreCase) ||
			           command.StartsWith("APPE ", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task UploadAsync_ConnectionDropsMidTransfer_RetriesAndResumesPartialFile()
	{
		await using var server = new DisposableFtpServer
		{
			FailNextUploadAfterBytes = 48 * 1024
		};
		using var temp = new TemporaryDirectory();
		var settings = new IntegrationSettings(server.BaseUri);
		var appData = new IntegrationAppDataProvider(temp.Path);
		var repository = new JsonVideoRecordRepository(appData, TimeProvider.System);
		var storage = new VideoStorageService(repository, appData, TimeProvider.System);
		var delay = new ImmediateDelay();
		var transfer = new FtpVideoTransferService(
			settings,
			repository,
			new FluentFtpClientFactory(settings),
			delay);
		var content = RandomNumberGenerator.GetBytes(256 * 1024);
		var record = await CreateRecordAsync(storage, content);

		await transfer.UploadAsync(record, null, CancellationToken.None);

		Assert.Equal(UploadState.Uploaded, record.UploadState);
		Assert.Equal(content, server.GetFile($"/uploads/{record.FileName}"));
		Assert.True(delay.Calls >= 1);
		Assert.Contains(
			server.Commands,
			command => command.StartsWith("REST ", StringComparison.OrdinalIgnoreCase) ||
			           command.StartsWith("APPE ", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task UploadAsync_InvalidCredentials_RealFluentFtpFailsAndRetainsLocalFile()
	{
		await using var server = new DisposableFtpServer();
		using var temp = new TemporaryDirectory();
		var settings = new IntegrationSettings(server.BaseUri)
		{
			Password = "wrong-password"
		};
		var appData = new IntegrationAppDataProvider(temp.Path);
		var repository = new JsonVideoRecordRepository(appData, TimeProvider.System);
		var storage = new VideoStorageService(repository, appData, TimeProvider.System);
		var delay = new ImmediateDelay();
		var transfer = new FtpVideoTransferService(
			settings,
			repository,
			new FluentFtpClientFactory(settings),
			delay);
		var record = await CreateRecordAsync(
			storage, RandomNumberGenerator.GetBytes(32 * 1024));

		await Assert.ThrowsAsync<IOException>(() =>
			transfer.UploadAsync(record, null, CancellationToken.None));

		Assert.Equal(UploadState.Failed, record.UploadState);
		Assert.True(File.Exists(record.LocalPath));
		Assert.Equal(2, delay.Calls);
		Assert.Contains(
			server.Commands,
			command => command.Equals(
				"PASS wrong-password", StringComparison.Ordinal));
	}

	[Fact]
	public async Task VerifyAsync_RemoteContentCorrupted_DoesNotPermitLocalDeletion()
	{
		await using var server = new DisposableFtpServer();
		using var temp = new TemporaryDirectory();
		var pipeline = CreatePipeline(server, temp);
		var content = RandomNumberGenerator.GetBytes(96 * 1024);
		var record = await CreateRecordAsync(pipeline.Storage, content);
		await pipeline.Transfer.UploadAsync(record, null, CancellationToken.None);
		var corrupted = content.ToArray();
		corrupted[corrupted.Length / 2] ^= 0xFF;
		server.SetFile($"/uploads/{record.FileName}", corrupted);

		var verification = await pipeline.Verification.VerifyAsync(
			record, CancellationToken.None);

		Assert.False(verification.IsVerified);
		Assert.Equal(VerificationState.Failed, record.VerificationState);
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			pipeline.Storage.DeleteLocalAsync(record, CancellationToken.None));
		Assert.True(File.Exists(record.LocalPath));
	}

	private static Pipeline CreatePipeline(
		DisposableFtpServer server, TemporaryDirectory temp)
	{
		var settings = new IntegrationSettings(server.BaseUri);
		var appData = new IntegrationAppDataProvider(temp.Path);
		var repository = new JsonVideoRecordRepository(appData, TimeProvider.System);
		var storage = new VideoStorageService(repository, appData, TimeProvider.System);
		var factory = new FluentFtpClientFactory(settings);
		return new Pipeline(
			repository,
			storage,
			new FtpVideoTransferService(
				settings, repository, factory, new ImmediateDelay()),
			new FtpTransferVerificationService(
				settings, storage, repository, factory));
	}

	private static async Task<VideoRecord> CreateRecordAsync(
		IVideoStorageService storage, byte[] content)
	{
		var pendingPath = storage.CreateSafeVideoPath();
		await File.WriteAllBytesAsync(pendingPath, content);
		return await storage.RegisterRecordingAsync(
			pendingPath,
			TimeSpan.FromMinutes(30),
			CancellationToken.None);
	}

	private sealed record Pipeline(
		JsonVideoRecordRepository Repository,
		IVideoStorageService Storage,
		IVideoTransferService Transfer,
		ITransferVerificationService Verification);
}
