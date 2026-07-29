using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SafeVideoTransfer.IntegrationTests.Support;

internal sealed class DisposableFtpServer : IAsyncDisposable
{
	private readonly TcpListener _controlListener = new(IPAddress.Loopback, 0);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly ConcurrentDictionary<string, byte[]> _files =
		new(StringComparer.Ordinal);
	private readonly ConcurrentBag<Task> _sessions = [];
	private readonly Task _acceptLoop;
	private int _failNextUploadAfterBytes;

	public DisposableFtpServer(
		string username = "integration-user",
		string password = "integration-password")
	{
		Username = username;
		Password = password;
		_controlListener.Start();
		Port = ((IPEndPoint)_controlListener.LocalEndpoint).Port;
		BaseUri = new Uri($"ftp://127.0.0.1:{Port}/uploads/");
		_acceptLoop = AcceptLoopAsync();
	}

	public string Username { get; }
	public string Password { get; }
	public int Port { get; }
	public Uri BaseUri { get; }
	public ConcurrentQueue<string> Commands { get; } = new();

	public int FailNextUploadAfterBytes
	{
		set => Interlocked.Exchange(ref _failNextUploadAfterBytes, value);
	}

	public void SetFile(string remotePath, byte[] content) =>
		_files[NormalizePath(remotePath, "/")] = content.ToArray();

	public byte[]? GetFile(string remotePath) =>
		_files.TryGetValue(NormalizePath(remotePath, "/"), out var content)
			? content.ToArray()
			: null;

	public async ValueTask DisposeAsync()
	{
		_shutdown.Cancel();
		_controlListener.Stop();
		try { await _acceptLoop; }
		catch (OperationCanceledException) { }
		catch (ObjectDisposedException) { }
		await Task.WhenAll(_sessions.ToArray()).WaitAsync(TimeSpan.FromSeconds(2));
		_shutdown.Dispose();
	}

	private async Task AcceptLoopAsync()
	{
		while (!_shutdown.IsCancellationRequested)
		{
			try
			{
				var client = await _controlListener.AcceptTcpClientAsync(_shutdown.Token);
				var session = HandleSessionAsync(client, _shutdown.Token);
				_sessions.Add(session);
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
			{
				break;
			}
			catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
			{
				break;
			}
		}
	}

	private async Task HandleSessionAsync(TcpClient controlClient, CancellationToken token)
	{
		using (controlClient)
		using (var controlStream = controlClient.GetStream())
		using (var reader = new StreamReader(
			       controlStream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
			       leaveOpen: true))
		using (var writer = new StreamWriter(
			       controlStream, Encoding.ASCII, leaveOpen: true)
		       {
			       NewLine = "\r\n",
			       AutoFlush = true
		       })
		{
			TcpListener? passiveListener = null;
			var currentDirectory = "/";
			long restartOffset = 0;
			var userAccepted = false;
			var authenticated = false;

			await writer.WriteLineAsync("220 SafeVideoTransfer integration FTP ready");
			while (!token.IsCancellationRequested)
			{
				var line = await reader.ReadLineAsync(token);
				if (line is null) break;
				Commands.Enqueue(line);
				var separator = line.IndexOf(' ');
				var command = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
				var argument = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();

				switch (command)
				{
					case "USER":
						userAccepted = string.Equals(argument, Username, StringComparison.Ordinal);
						await writer.WriteLineAsync("331 Password required");
						break;
					case "PASS":
						authenticated = userAccepted &&
						                string.Equals(argument, Password, StringComparison.Ordinal);
						await writer.WriteLineAsync(authenticated
							? "230 Login successful"
							: "530 Login incorrect");
						break;
					case "QUIT":
						await writer.WriteLineAsync("221 Goodbye");
						return;
					case "FEAT":
						await writer.WriteLineAsync("211-Features");
						await writer.WriteLineAsync(" EPSV");
						await writer.WriteLineAsync(" PASV");
						await writer.WriteLineAsync(" REST STREAM");
						await writer.WriteLineAsync(" SIZE");
						await writer.WriteLineAsync(" UTF8");
						await writer.WriteLineAsync("211 End");
						break;
					case "SYST":
						await writer.WriteLineAsync("215 UNIX Type: L8");
						break;
					case "CLNT":
					case "OPTS":
					case "TYPE":
					case "NOOP":
					case "SITE":
						await writer.WriteLineAsync("200 Command okay");
						break;
					case "PWD":
						await writer.WriteLineAsync($"257 \"{currentDirectory}\"");
						break;
					case "CWD":
						if (!authenticated)
						{
							await writer.WriteLineAsync("530 Not logged in");
							break;
						}
						currentDirectory = NormalizePath(argument, currentDirectory);
						await writer.WriteLineAsync("250 Directory changed");
						break;
					case "CDUP":
						currentDirectory = "/";
						await writer.WriteLineAsync("250 Directory changed");
						break;
					case "MKD":
						await writer.WriteLineAsync(
							$"257 \"{NormalizePath(argument, currentDirectory)}\" created");
						break;
					case "EPSV":
					case "PASV":
						passiveListener?.Stop();
						passiveListener = new TcpListener(IPAddress.Loopback, 0);
						passiveListener.Start();
						var passivePort =
							((IPEndPoint)passiveListener.LocalEndpoint).Port;
						if (command == "EPSV")
							await writer.WriteLineAsync(
								$"229 Entering Extended Passive Mode (|||{passivePort}|)");
						else
							await writer.WriteLineAsync(
								$"227 Entering Passive Mode (127,0,0,1,{passivePort / 256},{passivePort % 256})");
						break;
					case "SIZE":
					{
						var path = NormalizePath(argument, currentDirectory);
						await writer.WriteLineAsync(_files.TryGetValue(path, out var value)
							? $"213 {value.LongLength}"
							: "550 File unavailable");
						break;
					}
					case "MDTM":
						await writer.WriteLineAsync("213 20260729120000");
						break;
					case "REST":
						if (!long.TryParse(argument, out restartOffset))
							await writer.WriteLineAsync("501 Invalid restart position");
						else
							await writer.WriteLineAsync(
								$"350 Restarting at {restartOffset}");
						break;
					case "STOR":
					case "APPE":
						if (!authenticated)
						{
							await writer.WriteLineAsync("530 Not logged in");
							break;
						}
						var uploadListener = passiveListener;
						passiveListener = null;
						await ReceiveFileAsync(
							writer,
							uploadListener,
							NormalizePath(argument, currentDirectory),
							command == "APPE" ? -1 : restartOffset,
							token);
						restartOffset = 0;
						break;
					case "RETR":
						if (!authenticated)
						{
							await writer.WriteLineAsync("530 Not logged in");
							break;
						}
						var downloadListener = passiveListener;
						passiveListener = null;
						await SendFileAsync(
							writer,
							downloadListener,
							NormalizePath(argument, currentDirectory),
							restartOffset,
							token);
						restartOffset = 0;
						break;
					case "DELE":
						await writer.WriteLineAsync(
							_files.TryRemove(
								NormalizePath(argument, currentDirectory), out _)
								? "250 File deleted"
								: "550 File unavailable");
						break;
					case "ABOR":
						await writer.WriteLineAsync("226 Abort acknowledged");
						break;
					default:
						await writer.WriteLineAsync("502 Command not implemented");
						break;
				}
			}

			passiveListener?.Stop();
		}
	}

	private async Task ReceiveFileAsync(
		StreamWriter writer,
		TcpListener? passiveListener,
		string path,
		long restartOffset,
		CancellationToken token)
	{
		if (passiveListener is null)
		{
			await writer.WriteLineAsync("425 Use PASV or EPSV first");
			return;
		}

		await writer.WriteLineAsync("150 Opening binary data connection");
		using var dataClient = await passiveListener.AcceptTcpClientAsync(token);
		passiveListener.Stop();
		await using var dataStream = dataClient.GetStream();
		var existing = _files.TryGetValue(path, out var current) ? current : [];
		var offset = restartOffset < 0 ? existing.LongLength : restartOffset;
		offset = Math.Clamp(offset, 0, existing.LongLength);
		using var output = new MemoryStream();
		await output.WriteAsync(existing.AsMemory(0, checked((int)offset)), token);
		var buffer = new byte[16 * 1024];
		var failAfter = Interlocked.Exchange(ref _failNextUploadAfterBytes, 0);
		while (true)
		{
			var read = await dataStream.ReadAsync(buffer, token);
			if (read == 0) break;
			await output.WriteAsync(buffer.AsMemory(0, read), token);
			_files[path] = output.ToArray();
			if (failAfter > 0 && output.Length - offset >= failAfter)
			{
				dataClient.Client.LingerState = new LingerOption(true, 0);
				dataClient.Close();
				await writer.WriteLineAsync("426 Connection closed; transfer aborted");
				return;
			}
		}

		_files[path] = output.ToArray();
		await writer.WriteLineAsync("226 Transfer complete");
	}

	private async Task SendFileAsync(
		StreamWriter writer,
		TcpListener? passiveListener,
		string path,
		long restartOffset,
		CancellationToken token)
	{
		if (passiveListener is null)
		{
			await writer.WriteLineAsync("425 Use PASV or EPSV first");
			return;
		}
		if (!_files.TryGetValue(path, out var content))
		{
			await writer.WriteLineAsync("550 File unavailable");
			passiveListener.Stop();
			return;
		}

		await writer.WriteLineAsync("150 Opening binary data connection");
		using var dataClient = await passiveListener.AcceptTcpClientAsync(token);
		passiveListener.Stop();
		await using var dataStream = dataClient.GetStream();
		var offset = checked((int)Math.Clamp(restartOffset, 0, content.LongLength));
		await dataStream.WriteAsync(content.AsMemory(offset), token);
		await dataStream.FlushAsync(token);
		dataClient.Client.Shutdown(SocketShutdown.Send);
		await writer.WriteLineAsync("226 Transfer complete");
	}

	private static string NormalizePath(string path, string currentDirectory)
	{
		var decoded = Uri.UnescapeDataString(path.Trim());
		if (string.IsNullOrEmpty(decoded)) return currentDirectory;
		var combined = decoded.StartsWith('/')
			? decoded
			: $"{currentDirectory.TrimEnd('/')}/{decoded}";
		var parts = new Stack<string>();
		foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (part == ".") continue;
			if (part == "..")
			{
				if (parts.Count > 0) parts.Pop();
				continue;
			}
			parts.Push(part);
		}
		return "/" + string.Join('/', parts.Reverse());
	}
}
