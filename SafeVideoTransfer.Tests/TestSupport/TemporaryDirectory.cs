namespace SafeVideoTransfer.Tests.TestSupport;

internal sealed class TemporaryDirectory : IDisposable
{
	public TemporaryDirectory()
	{
		Path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"SafeVideoTransfer.Tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path);
	}

	public string Path { get; }

	public string CreateFile(string name, byte[]? content = null)
	{
		var path = System.IO.Path.Combine(Path, name);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content ?? [1, 2, 3, 4]);
		return path;
	}

	public void Dispose()
	{
		if (Directory.Exists(Path))
			Directory.Delete(Path, recursive: true);
	}
}
