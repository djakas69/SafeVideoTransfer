using System.Text.Json;
using SafeVideoTransfer.Models;

namespace SafeVideoTransfer.Services;

public sealed class JsonVideoRecordRepository : IVideoRecordRepository
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly string _indexPath = Path.Combine(FileSystem.AppDataDirectory, "video-index.json");
	private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

	public async Task<IReadOnlyList<VideoRecord>> GetAllAsync(CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			return await ReadUnsafeAsync(cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task UpsertAsync(VideoRecord record, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var records = await ReadUnsafeAsync(cancellationToken);
			var existing = records.FindIndex(x => x.Id == record.Id);
			record.UpdatedAtUtc = DateTimeOffset.UtcNow;
			if (existing >= 0) records[existing] = record;
			else records.Add(record);

			Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
			var temporaryPath = _indexPath + ".tmp";
			await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
				await JsonSerializer.SerializeAsync(stream, records, _jsonOptions, cancellationToken);
			File.Move(temporaryPath, _indexPath, true);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<List<VideoRecord>> ReadUnsafeAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_indexPath)) return [];
		try
		{
			await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			return await JsonSerializer.DeserializeAsync<List<VideoRecord>>(stream, _jsonOptions, cancellationToken) ?? [];
		}
		catch (JsonException)
		{
			File.Move(_indexPath, _indexPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", true);
			return [];
		}
	}
}
