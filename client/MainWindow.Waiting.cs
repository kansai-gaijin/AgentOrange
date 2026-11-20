using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// wait_for, Agent Orange's tool for waiting: until a file appears or changes, a file has some text
/// (such as a log line), a port takes connections, or a process ends. Agents otherwise try sleep,
/// which Claude Code refuses, then loop in the shell and read near-empty "still waiting" lines turn
/// after turn, or sit in one call until the shell's ten-minute limit. It always answers within the
/// time it's given, with how things stood, so another call carries on the wait.
/// </summary>
public partial class MainWindow
{
    private const int LongestWait = 300;

    private McpTool WaitForTool() => new("wait_for",
        "Waits, instead of sleep or a loop in the shell: until a file appears or changes, a file has some text (such as a log line), a port on this PC takes connections, or a process ends. Give one of file, port or process. "
        + $"It answers once that happens, or after seconds (60 if not given, {LongestWait} at most) with how things stood, so call it again to keep waiting.",
        new JsonObject
        {
            ["file"] = Text("A file, by its path in the project's folder or a whole path. Alone, waits for it to appear, or to change if it's there already."),
            ["text"] = Text("With file: waits until the file has this text, such as a log line. Not case-sensitive. Only what's written once the wait starts counts, unless existing is true."),
            ["existing"] = Flag("With text: count what's in the file already, too."),
            ["port"] = Text("A port on this PC, such as 8080, or host:port: waits until it takes connections."),
            ["process"] = Text("A process, by its name (such as UnrealEditor or blender.exe) or its id: waits until it has ended."),
            ["seconds"] = Number($"The longest to wait, 60 if not given, {LongestWait} at most."),
        },
        [], (_, _) => "", (project, given) => WaitForAsync(project, given));

    private async Task<object> WaitForAsync(Project project, JsonObject given)
    {
        var file = Given(given, "file") is { Length: > 0 } named ? named : null;
        var port = Given(given, "port") is { Length: > 0 } address ? address : null;
        var process = Given(given, "process") is { Length: > 0 } program ? program : null;
        var text = Given(given, "text") is { Length: > 0 } words ? words : null;
        var asked = new[] { file, port, process }.Count(each => each is not null);
        if (asked != 1)
            throw new McpException(-32602, $"wait_for waits for one thing: give one of file, port or process{(asked > 1 ? ", not more than one" : "")}. With file, text waits for words in it, such as a log line.");
        if (text is not null && file is null) throw new McpException(-32602, "text is only for waiting on a file: give file too.");
        var seconds = WholeNumber(given, "seconds") ?? 60;
        if (seconds < 1 || seconds > LongestWait) throw new McpException(-32602, $"seconds must be from 1 to {LongestWait}; it was {seconds}. A longer wait is another call to wait_for.");

        Func<(bool Done, string Said)> look;
        if (file is not null)
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(ProjectFolder(project.Id) ?? throw new McpException(-32602, $"file: {file} is a path in the project's folder, and this project has no folder on this PC. Give a whole path."), file);
            look = text is null ? FileChange(path, file) : FileText(path, file, text, Yes(given, "existing"));
        }
        else if (port is not null)
        {
            var (host, number) = port.Split(':') is [var h, var p] ? (h, p) : ("127.0.0.1", port);
            if (!int.TryParse(number, out var portNumber) || portNumber is < 1 or > 65535) throw new McpException(-32602, $"port must be a number from 1 to 65535, or host:port, such as 8080 or 127.0.0.1:8080; it was {port}.");
            look = () => Answers(host, portNumber) ? (true, $"{host}:{portNumber} takes connections") : (false, $"{host}:{portNumber} doesn't take connections yet");
        }
        else look = ProcessEnd(process!);

        var watch = Stopwatch.StartNew();
        return await Task.Run(async () =>
        {
            while (true)
            {
                var (done, said) = look();
                var took = watch.Elapsed.TotalSeconds;
                if (done) return $"Done after {(took < 10 ? took.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : took.ToString("0", System.Globalization.CultureInfo.InvariantCulture))} s: {said}.";
                if (watch.Elapsed.TotalSeconds >= seconds) return $"Still waiting after {seconds} s: {said}. Call wait_for again to keep waiting.";
                await Task.Delay(250);
            }
        });
    }

    private static bool Answers(string host, int port)
    {
        try
        {
            using var socket = new TcpClient();
            return socket.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(1)) && socket.Connected;
        }
        catch (Exception error) when (error is SocketException or AggregateException or ArgumentException)
        {
            return false;
        }
    }

    // There, or changed since the wait began.
    private static Func<(bool, string)> FileChange(string path, string named)
    {
        var before = File.Exists(path) ? new FileInfo(path) : null;
        var (length, written) = before is null ? (-1L, DateTime.MinValue) : (before.Length, before.LastWriteTimeUtc);
        return () =>
        {
            var now = new FileInfo(path);
            if (!now.Exists) return (false, $"{named} isn't there yet");
            if (before is null) return (true, $"{named} appeared ({now.Length} bytes)");
            if (now.Length != length || now.LastWriteTimeUtc != written) return (true, $"{named} changed (now {now.Length} bytes)");
            return (false, $"{named} hasn't changed ({now.Length} bytes, last written {now.LastWriteTime:HH:mm:ss})");
        };
    }

    // The text, in what's written after the wait began, or anywhere in the file. Only what's new is read each look.
    private static Func<(bool, string)> FileText(string path, string named, string text, bool existing)
    {
        long read = existing || !File.Exists(path) ? 0 : new FileInfo(path).Length;
        var tail = "";
        var lastLine = read > 0 ? LastLineOf(path) : "";
        return () =>
        {
            try
            {
                if (!File.Exists(path)) return (false, $"{named} isn't there yet");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < read) read = 0; // Started again, as a log is when its program restarts.
                stream.Seek(read, SeekOrigin.Begin);
                var bytes = new byte[stream.Length - read];
                stream.ReadExactly(bytes);
                read += bytes.Length;

                // What came before, as far as a line back, so text split across two looks is still found.
                var fresh = tail + Encoding.UTF8.GetString(bytes);
                var lines = fresh.Split('\n');
                if (lines.Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) is { } last) lastLine = last;
                tail = lines[^1].Length > 2000 ? lines[^1][^2000..] : lines[^1];
                if (lines.FirstOrDefault(line => line.Contains(text, StringComparison.OrdinalIgnoreCase)) is { } found)
                    return (true, $"{named} has it: \"{Shortened(found.Trim(), 300)}\"");
                return (false, lastLine.Length > 0 ? $"{named} doesn't have \"{text}\" yet. Its last line: \"{Shortened(lastLine, 200)}\"" : $"{named} doesn't have \"{text}\" yet");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (false, $"{named} couldn't be read just then: {error.Message}");
            }
        };
    }

    // What a file ends with, from its last few kilobytes, so a wait that has seen nothing new can still say where it's got to.
    private static string LastLineOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var from = Math.Max(0, stream.Length - 4096);
            stream.Seek(from, SeekOrigin.Begin);
            var bytes = new byte[stream.Length - from];
            stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes).Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Shortened(string text, int most) => text.Length > most ? text[..(most - 1)] + "…" : text;

    // Ended: by its id, or once none of that name is running.
    private static Func<(bool, string)> ProcessEnd(string asked)
    {
        if (int.TryParse(asked, out var id))
        {
            return () =>
            {
                try
                {
                    using var running = Process.GetProcessById(id);
                    return running.HasExited ? (true, $"process {id} has ended") : (false, $"process {id} ({running.ProcessName}) is still running");
                }
                catch (ArgumentException)
                {
                    return (true, $"process {id} has ended");
                }
            };
        }
        var name = asked.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? asked[..^4] : asked;
        return () =>
        {
            var running = Process.GetProcessesByName(name);
            try
            {
                return running.Length == 0
                    ? (true, $"no process called {name} is running")
                    : (false, $"{name} is still running ({(running.Length == 1 ? "id" : "ids")} {string.Join(", ", running.Select(each => each.Id))})");
            }
            finally
            {
                foreach (var each in running) each.Dispose();
            }
        };
    }

    // A whole number, given as one or as words, as some agents send numbers.
    private static int? WholeNumber(JsonObject given, string name)
    {
        if (given[name] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        if (value.TryGetValue<double>(out var real) && real == Math.Floor(real)) return (int)real;
        if (value.TryGetValue<string>(out var words) && int.TryParse(words.Trim(), out var parsed)) return parsed;
        throw new McpException(-32602, $"{name} must be a whole number; it was {value.ToJsonString()}.");
    }

    // A yes or no, given as one or as words.
    private static bool Yes(JsonObject given, string name) =>
        given[name] is JsonValue value && (value.TryGetValue<bool>(out var yes) ? yes : value.TryGetValue<string>(out var words) && words.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}
