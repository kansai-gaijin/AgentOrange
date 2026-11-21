using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentOrange;

/// <summary>
/// Agent sessions that outlive Agent Orange (#101). A session's agent program isn't the app's child: a
/// small host is, a copy of Agent Orange started with --session-host from the copy of the program
/// (<see cref="AppCopy"/>), on its own (<see cref="DetachedProcess"/>). The host starts the agent
/// program, writes each line it says to out.jsonl in the session's folder as it says it, and passes on
/// what the app sends it over a pipe only this Windows account can open. So when the app restarts, for
/// an update or a dev build's rebuild, the agent goes on working, and the app picks the session back up
/// from the file where it left off (<see cref="HostedSession"/>).
///
/// A session's folder holds start.json (what to start, from the app), host.json (the host's process,
/// from the host), out.jsonl and err.log (what the agent program says), and exit.json once it's ended.
/// The host ends the agent program once it's been left alone for half an hour, with no app connected
/// and nothing said, so one the app never comes back for doesn't run for ever.
/// </summary>
internal static class SessionHost
{
    public const string Argument = "--session-host";

    private static readonly TimeSpan LeftAlone = TimeSpan.FromMinutes(30);

    internal sealed record StartSpec(string Program, string Arguments, string Folder, Dictionary<string, string?> Environment, string Pipe);

    internal sealed record HostInfo(int HostId, DateTimeOffset HostStarted, int ProgramId);

    internal sealed record ExitInfo(int Code);

    internal static string StartFile(string folder) => Path.Combine(folder, "start.json");
    internal static string HostFile(string folder) => Path.Combine(folder, "host.json");
    internal static string OutFile(string folder) => Path.Combine(folder, "out.jsonl");
    internal static string ErrorFile(string folder) => Path.Combine(folder, "err.log");
    internal static string ExitFile(string folder) => Path.Combine(folder, "exit.json");

    /// <summary>In the copy started with --session-host: runs the session in this folder until its agent program ends.</summary>
    public static int Run(string folder)
    {
        StartSpec spec;
        try
        {
            spec = JsonSerializer.Deserialize<StartSpec>(File.ReadAllText(StartFile(folder))) ?? throw new JsonException("Nothing to start.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return 2;
        }

        var start = new ProcessStartInfo(spec.Program, spec.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.Folder,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment.Clear();
        foreach (var (name, value) in spec.Environment) start.Environment[name] = value;

        Process program;
        try
        {
            program = Process.Start(start) ?? throw new IOException($"{spec.Program} didn't start.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            File.AppendAllText(ErrorFile(folder), $"Couldn't start {spec.Program}: {error.Message}\n");
            File.WriteAllText(ExitFile(folder), JsonSerializer.Serialize(new ExitInfo(-1)));
            return 1;
        }

        // It ends with the host, whatever ends the host.
        ChildProcesses.Track(program);
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(HostFile(folder), JsonSerializer.Serialize(new HostInfo(self.Id, self.StartTime, program.Id)));

        var lastHeard = DateTimeOffset.UtcNow.Ticks;
        var lastConnected = DateTimeOffset.UtcNow.Ticks;
        var connected = 0;
        var output = Task.Run(() => Pump(program.StandardOutput, OutFile(folder), () => Interlocked.Exchange(ref lastHeard, DateTimeOffset.UtcNow.Ticks)));
        var errors = Task.Run(() => Pump(program.StandardError, ErrorFile(folder), null));
        _ = Task.Run(async () =>
        {
            while (!program.HasExited)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(spec.Pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync();
                    Interlocked.Exchange(ref connected, 1);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        await program.StandardInput.WriteLineAsync(line);
                        await program.StandardInput.FlushAsync();
                    }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    // The app went, or the agent program did.
                }
                Interlocked.Exchange(ref connected, 0);
                Interlocked.Exchange(ref lastConnected, DateTimeOffset.UtcNow.Ticks);
                await Task.Delay(100);
            }
        });

        while (!program.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            var now = DateTimeOffset.UtcNow.Ticks;
            if (Volatile.Read(ref connected) == 0 && now - Volatile.Read(ref lastHeard) > LeftAlone.Ticks && now - Volatile.Read(ref lastConnected) > LeftAlone.Ticks)
            {
                File.AppendAllText(ErrorFile(folder), "Ended, as Agent Orange didn't come back for it for half an hour.\n");
                program.Kill(entireProcessTree: true);
            }
        }
        Task.WaitAll([output, errors], TimeSpan.FromSeconds(10));
        File.WriteAllText(ExitFile(folder), JsonSerializer.Serialize(new ExitInfo(program.ExitCode)));
        return 0;
    }

    // Each line as it's said, so the app reads it straight away, and nothing is lost while it's away.
    private static void Pump(StreamReader from, string file, Action? heard)
    {
        using var to = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            while (from.ReadLine() is { } line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                to.Write(bytes);
                to.Flush();
                heard?.Invoke();
            }
        }
        catch (IOException)
        {
            // The agent program ended partway through a line.
        }
    }
}

/// <summary>
/// The app's side of a hosted session (<see cref="SessionHost"/>): reads what the agent program says from
/// its folder's out.jsonl, a line at a time, from where the app had got to, and sends it lines over the
/// host's pipe, connecting again whenever that's needed. Offset is how far the app has read, which the
/// conversation keeps, so after a restart it picks up from there. Only one copy of the app takes a
/// session at a time.
/// </summary>
internal sealed class HostedSession
{
    private static readonly TimeSpan HostWaitsFor = TimeSpan.FromSeconds(30);

    private readonly string _folder;
    private readonly string _pipe;
    private readonly Channel<string> _toSend = Channel.CreateUnbounded<string>();
    private readonly Mutex _claim;
    private FileStream? _out;
    private byte[] _buffer = new byte[64 * 1024];
    private int _buffered; // Bytes read past Offset that aren't a whole line yet.
    private int? _hostId;
    private DateTimeOffset? _hostStarted;
    private volatile string? _problem; // Why it couldn't be started.
    private volatile bool _ended;
    private bool _killed;
    private DateTimeOffset _launchedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lookedAt;

    /// <summary>The session's own folder, which the conversation keeps to pick it back up.</summary>
    public string Folder => _folder;

    /// <summary>How much of out.jsonl has been read, in bytes, up to the end of a line.</summary>
    public long Offset { get; private set; }

    private HostedSession(string folder, string pipe, long offset, Mutex claim)
    {
        _folder = folder;
        _pipe = pipe;
        Offset = offset;
        _claim = claim;
        _ = SendAsync();
    }

    private static Mutex Claim(string folder, out bool ours) =>
        new(false, $"AgentOrange-Session-{Path.GetFileName(Path.GetDirectoryName(folder))}-{Path.GetFileName(folder)}", out ours);

    /// <summary>
    /// Starts the agent program in a session of its own in a new folder under sessions (one for each
    /// conversation), ending whatever earlier session of it is left there. The host is started out of
    /// the way, and anything sent meanwhile waits for it.
    /// </summary>
    public static HostedSession Start(ProcessStartInfo start, string sessions, string hostProgram)
    {
        Directory.CreateDirectory(sessions);
        foreach (var earlier in Directory.EnumerateDirectories(sessions)) EndAndRemove(earlier);
        var folder = Path.Combine(sessions, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(folder);
        var pipe = $"AgentOrange-Session-{Guid.NewGuid():N}";
        var environment = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        File.WriteAllText(SessionHost.StartFile(folder), JsonSerializer.Serialize(new SessionHost.StartSpec(start.FileName, start.Arguments, start.WorkingDirectory, environment, pipe)));
        var session = new HostedSession(folder, pipe, 0, Claim(folder, out _));
        _ = session.LaunchAsync(hostProgram);
        return session;
    }

    private async Task LaunchAsync(string hostProgram)
    {
        var (id, problem) = await DetachedProcess.StartAsync(hostProgram, [SessionHost.Argument, _folder], _folder);
        if (id is null)
        {
            _problem = $"Couldn't start its host: {problem}";
            return;
        }
        _hostId = id;
        _launchedAt = DateTimeOffset.UtcNow;
        if (_killed) Kill();
    }

    /// <summary>
    /// After a restart: the session in this folder, read from offset on, when its host is still running
    /// or it said more before it ended. Null when there's nothing to pick up, or another copy of the app has it.
    /// </summary>
    public static HostedSession? PickUp(string folder, long offset)
    {
        if (!Directory.Exists(folder) || ReadHost(folder) is not { } host) return null;
        var alive = IsAlive(host.HostId, host.HostStarted);
        var unread = File.Exists(SessionHost.OutFile(folder)) && new FileInfo(SessionHost.OutFile(folder)).Length > offset;
        if (!alive && !unread) return null;
        var claim = Claim(folder, out var ours);
        if (!ours)
        {
            claim.Dispose();
            return null;
        }
        string pipe;
        try
        {
            pipe = JsonSerializer.Deserialize<SessionHost.StartSpec>(File.ReadAllText(SessionHost.StartFile(folder)))?.Pipe ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            pipe = "";
        }
        return new HostedSession(folder, pipe, offset, claim) { _hostId = host.HostId, _hostStarted = host.HostStarted, WentOn = alive };
    }

    /// <summary>Picked back up with its host still running, rather than only to read what it said before it ended.</summary>
    public bool WentOn { get; private init; }

    /// <summary>The next line the agent program said, waiting for one, or null once it has ended and everything it said is read.</summary>
    public async Task<string?> ReadLineAsync()
    {
        for (var quiet = 0; ; quiet++)
        {
            if (NextLine() is { } line) return line;
            if (_ended)
            {
                Close();
                return null;
            }
            if (HasEnded())
            {
                _ended = true; // Once more round, for what it said last.
                continue;
            }
            await Task.Delay(quiet < 40 ? 25 : 200); // Quickly while it's talking, less often once it's quiet.
        }
    }

    private string? NextLine()
    {
        while (true)
        {
            var end = Array.IndexOf(_buffer, (byte)'\n', 0, _buffered);
            if (end >= 0)
            {
                var line = Encoding.UTF8.GetString(_buffer, 0, end).TrimEnd('\r');
                Buffer.BlockCopy(_buffer, end + 1, _buffer, 0, _buffered - end - 1);
                _buffered -= end + 1;
                Offset += end + 1;
                return line;
            }
            if (_out is null)
            {
                if (!File.Exists(SessionHost.OutFile(_folder))) return null;
                try
                {
                    _out = new FileStream(SessionHost.OutFile(_folder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException)
                {
                    return null;
                }
            }
            if (_buffered == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            _out.Position = Offset + _buffered;
            var read = _out.Read(_buffer, _buffered, _buffer.Length - _buffered);
            if (read == 0) return null;
            _buffered += read;
        }
    }

    // Looked at twice a second at most: its exit written, or its host gone.
    private bool HasEnded()
    {
        if (_problem is not null) return true;
        if (DateTimeOffset.UtcNow - _lookedAt < TimeSpan.FromMilliseconds(500)) return false;
        _lookedAt = DateTimeOffset.UtcNow;
        if (File.Exists(SessionHost.ExitFile(_folder))) return true;
        if (_hostStarted is null && ReadHost(_folder) is { } host)
        {
            _hostId = host.HostId;
            _hostStarted = host.HostStarted;
        }
        if (_hostId is not { } id) return false; // Still starting.
        if (!IsAlive(id, _hostStarted)) return true;
        // Started, but it never got as far as starting the agent program.
        return _hostStarted is null && DateTimeOffset.UtcNow - _launchedAt > HostWaitsFor;
    }

    /// <summary>Sends a line to the agent program. It waits its turn, and for the host, which may still be starting.</summary>
    public void WriteLine(string line) => _toSend.Writer.TryWrite(line);

    private async Task SendAsync()
    {
        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;
        await foreach (var line in _toSend.Reader.ReadAllAsync())
        {
            for (var tries = 0; !_ended && !_killed && _problem is null && tries < 120; tries++)
            {
                try
                {
                    if (writer is null)
                    {
                        if (_pipe.Length == 0) break;
                        pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.ConnectAsync(500);
                        writer = new StreamWriter(pipe, new UTF8Encoding(false));
                    }
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync();
                    break;
                }
                catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    writer = null;
                    pipe?.Dispose();
                    pipe = null;
                    await Task.Delay(250);
                }
            }
        }
        pipe?.Dispose();
    }

    /// <summary>Ends the agent program, and everything it started, with its host.</summary>
    public void Kill()
    {
        _killed = true;
        if (_hostId is not { } id) return; // Ended as soon as it has started.
        try
        {
            using var host = Process.GetProcessById(id);
            if (_hostStarted is null || SameStart(host, _hostStarted.Value)) host.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>What it said on its error output, the end of it, and why it couldn't start.</summary>
    public string Errors()
    {
        var said = _problem ?? "";
        try
        {
            var file = SessionHost.ErrorFile(_folder);
            if (File.Exists(file))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Position = Math.Max(0, stream.Length - 4000);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                said = $"{said}\n{reader.ReadToEnd()}";
            }
        }
        catch (IOException)
        {
        }
        return said.Trim();
    }

    private void Close()
    {
        _toSend.Writer.TryComplete();
        _out?.Dispose();
        _out = null;
        _claim.Dispose();
    }

    private static SessionHost.HostInfo? ReadHost(string folder)
    {
        try
        {
            return File.Exists(SessionHost.HostFile(folder)) ? JsonSerializer.Deserialize<SessionHost.HostInfo>(File.ReadAllText(SessionHost.HostFile(folder))) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // Still being written.
        }
    }

    private static bool IsAlive(int id, DateTimeOffset? started)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && (started is null || SameStart(process, started.Value));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    // The same process, not another given its id since.
    private static bool SameStart(Process process, DateTimeOffset started) =>
        Math.Abs((new DateTimeOffset(process.StartTime) - started).TotalSeconds) < 2;

    // An earlier session of the conversation, ended if it's still running, and its folder removed.
    private static void EndAndRemove(string folder)
    {
        if (ReadHost(folder) is { } host && IsAlive(host.HostId, host.HostStarted))
        {
            try
            {
                using var process = Process.GetProcessById(host.HostId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
            {
            }
        }
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Its host is still letting go of it. It goes with the next.
        }
    }
}
