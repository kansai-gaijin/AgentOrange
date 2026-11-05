using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>Standard MCP over HTTP, or a program that only offers a port on this PC.</summary>
public enum McpKind { Http, Port }

public enum McpState { NotSet, NotRunning, Connected, NeedsLogin, PortOpen, NotMcp }

public sealed record EditorDefinition(string Id, string Name, bool IsEngine, McpKind DefaultKind, string DefaultAddress);

public sealed record EditorSettings(string ProgramPath, McpKind Kind, string Address);

public sealed record EditorInstance(int ProcessId, string WindowTitle, bool HoldsMcp);

public sealed record EditorStatus(bool ProgramFound, IReadOnlyList<EditorInstance> Instances, McpState Mcp, string? McpHolder);

/// <summary>
/// Keeps track of every engine and tool: how many copies are open, and whether
/// its MCP is answering. It runs all the time, so the app always knows. Open
/// ports are read from Windows rather than by connecting to them, so checking
/// doesn't disturb the editors. A full MCP handshake only runs when something
/// changes, plus an occasional recheck, so sessions don't pile up inside them.
/// </summary>
public sealed partial class EditorMonitor
{
    private static readonly TimeSpan HandshakeRecheck = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static readonly EditorDefinition[] Definitions =
    [
        new("unreal", "Unreal Engine", true, McpKind.Http, "http://localhost:8666/uemcp"),
        new("godot", "Godot", true, McpKind.Http, "http://127.0.0.1:8645/mcp"),
        new("blender", "Blender", false, McpKind.Port, "127.0.0.1:9876"),
        new("aseprite", "Aseprite", false, McpKind.Http, "http://127.0.0.1:8650/mcp"),
    ];

    private readonly LocalDatabase _database;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, EditorStatus> _statuses = [];
    private readonly Dictionary<string, Handshake> _handshakes = [];
    private bool _checking;

    private sealed record Handshake(string Address, int OwnerId, McpState State, DateTimeOffset At);

    public EditorMonitor(LocalDatabase database)
    {
        _database = database;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) => await CheckAsync();
        timer.Start();
        _ = CheckAsync();
    }

    public event Action? Changed;

    public EditorStatus? StatusOf(string id) => _statuses.GetValueOrDefault(id);

    public EditorSettings SettingsOf(EditorDefinition editor)
    {
        var stored = _database.GetSetting(SettingKey(editor));
        return string.IsNullOrEmpty(stored)
            ? new EditorSettings(FindProgram(editor.Id) ?? "", editor.DefaultKind, editor.DefaultAddress)
            : JsonSerializer.Deserialize<EditorSettings>(stored, JsonOptions)!;
    }

    public void Save(EditorDefinition editor, EditorSettings settings)
    {
        _database.SetSetting(SettingKey(editor), JsonSerializer.Serialize(settings, JsonOptions));
        _handshakes.Remove(editor.Id);
        _ = CheckAsync();
    }

    public void ResetToDefaults(bool engines)
    {
        foreach (var editor in Definitions.Where(editor => editor.IsEngine == engines))
        {
            _database.SetSetting(SettingKey(editor), "");
            _handshakes.Remove(editor.Id);
        }
        _ = CheckAsync();
    }

    /// <summary>Accepts only this PC's addresses, because editor traffic carries project data.</summary>
    public static bool TryCleanAddress(McpKind kind, string input, out string address)
    {
        var text = input.Trim();
        address = text;
        if (text.Length == 0) return true;

        if (kind == McpKind.Http)
        {
            if (!text.Contains("://")) text = "http://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !uri.IsLoopback)
            {
                return false;
            }
            address = text;
            return true;
        }

        if (int.TryParse(text, out var justPort)) text = $"127.0.0.1:{justPort}";
        if (!Uri.TryCreate($"tcp://{text}", UriKind.Absolute, out var host) || !host.IsLoopback || host.IsDefaultPort || host.Port <= 0)
        {
            return false;
        }
        address = text;
        return true;
    }

    public static int PortOf(McpKind kind, string address)
    {
        if (address.Length == 0) return -1;
        var text = kind == McpKind.Http ? address : $"tcp://{address}";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Port : -1;
    }

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;

        try
        {
            var settings = Definitions.ToDictionary(editor => editor.Id, SettingsOf);
            var known = new Dictionary<string, Handshake>(_handshakes);
            var (statuses, handshakes) = await Task.Run(() => CheckAllAsync(settings, known));

            foreach (var (id, handshake) in handshakes) _handshakes[id] = handshake;
            var changed = false;
            foreach (var (id, status) in statuses)
            {
                if (_statuses.TryGetValue(id, out var previous) && Same(previous, status)) continue;
                _statuses[id] = status;
                changed = true;
            }
            if (changed) Changed?.Invoke();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Write($"Couldn't check editors: {error.Message}");
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task<(Dictionary<string, EditorStatus>, Dictionary<string, Handshake>)> CheckAllAsync(
        Dictionary<string, EditorSettings> settings,
        Dictionary<string, Handshake> known)
    {
        var listeners = ReadListeners();
        var statuses = new Dictionary<string, EditorStatus>();
        var handshakes = new Dictionary<string, Handshake>();

        foreach (var editor in Definitions)
        {
            var current = settings[editor.Id];
            var port = PortOf(current.Kind, current.Address);
            var owner = port > 0 && listeners.TryGetValue(port, out var id) ? id : -1;

            var state = McpState.NotSet;
            if (port > 0)
            {
                if (owner < 0)
                {
                    state = McpState.NotRunning;
                }
                else if (current.Kind == McpKind.Port)
                {
                    state = McpState.PortOpen;
                }
                else if (known.TryGetValue(editor.Id, out var last)
                         && last.Address == current.Address
                         && last.OwnerId == owner
                         && DateTimeOffset.UtcNow - last.At < HandshakeRecheck)
                {
                    state = last.State;
                }
                else
                {
                    state = await HandshakeAsync(current.Address);
                    handshakes[editor.Id] = new Handshake(current.Address, owner, state, DateTimeOffset.UtcNow);
                }
            }

            var instances = FindInstances(current.ProgramPath, owner);
            var holder = owner >= 0 && instances.All(instance => instance.ProcessId != owner) ? ProcessName(owner) : null;
            statuses[editor.Id] = new EditorStatus(File.Exists(current.ProgramPath), instances, state, holder);
        }

        return (statuses, handshakes);
    }

    private async Task<McpState> HandshakeAsync(string address)
    {
        const string initialize = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"agent-orange-status","version":"1"}}}
            """;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, address)
            {
                Content = new StringContent(initialize, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return McpState.NeedsLogin;
            if (!response.IsSuccessStatusCode) return McpState.NotMcp;

            // The answer may arrive on a stream that stays open, so read only until it appears.
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var isMcp = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!line.Contains("\"protocolVersion\"")) continue;
                isMcp = true;
                break;
            }

            await EndSessionAsync(address, response);
            return isMcp ? McpState.Connected : McpState.NotMcp;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            return McpState.NotMcp;
        }
    }

    // This was only a check, so leave nothing behind in the editor.
    private async Task EndSessionAsync(string address, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) return;
        try
        {
            using var end = new HttpRequestMessage(HttpMethod.Delete, address);
            end.Headers.TryAddWithoutValidation("Mcp-Session-Id", ids.First());
            using var _ = await _http.SendAsync(end);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // The session ends by itself eventually.
        }
    }

    private static List<EditorInstance> FindInstances(string programPath, int mcpOwner)
    {
        var instances = new List<EditorInstance>();
        if (programPath.Length == 0) return instances;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(programPath)))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, programPath, StringComparison.OrdinalIgnoreCase)) continue;
                    instances.Add(new EditorInstance(process.Id, process.MainWindowTitle, process.Id == mcpOwner));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    // It closed while being read, or belongs to another user.
                }
            }
        }
        return instances.OrderBy(instance => instance.ProcessId).ToList();
    }

    private static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Same(EditorStatus a, EditorStatus b) =>
        a.ProgramFound == b.ProgramFound && a.Mcp == b.Mcp && a.McpHolder == b.McpHolder && a.Instances.SequenceEqual(b.Instances);

    private static string SettingKey(EditorDefinition editor) => $"editor:{editor.Id}";

    // Usual install locations. People can point at anything else.
    private static string? FindProgram(string id)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common");

        return id switch
        {
            "unreal" => NewestChild(Path.Combine(programFiles, "Epic Games"), "UE_*", Path.Combine("Engine", "Binaries", "Win64", "UnrealEditor.exe")),
            "godot" => FirstExisting(Path.Combine(steam, "Godot Engine", "godot.windows.opt.tools.64.exe")),
            "blender" => NewestChild(Path.Combine(programFiles, "Blender Foundation"), "Blender*", "blender.exe")
                ?? FirstExisting(Path.Combine(steam, "Blender", "blender.exe")),
            "aseprite" => FirstExisting(Path.Combine(programFiles, "Aseprite", "aseprite.exe"), Path.Combine(steam, "Aseprite", "aseprite.exe")),
            _ => null,
        };
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? NewestChild(string folder, string pattern, string relativePath)
    {
        if (!Directory.Exists(folder)) return null;
        return Directory.GetDirectories(folder, pattern)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, relativePath))
            .FirstOrDefault(File.Exists);
    }

    // Which process is listening on each port, as Windows reports it.

    private const int IPv4 = 2;
    private const int IPv6 = 23;
    private const int ListenersWithOwner = 3;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);

    private static Dictionary<int, int> ReadListeners()
    {
        var listeners = new Dictionary<int, int>();
        ReadListeners(IPv4, rowSize: 24, portOffset: 8, ownerOffset: 20, listeners);
        ReadListeners(IPv6, rowSize: 56, portOffset: 20, ownerOffset: 52, listeners);
        return listeners;
    }

    private static void ReadListeners(int family, int rowSize, int portOffset, int ownerOffset, Dictionary<int, int> listeners)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, ListenersWithOwner, 0);
        if (size == 0) return;

        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, family, ListenersWithOwner, 0) != 0) return;

            var count = Marshal.ReadInt32(table);
            for (var index = 0; index < count; index++)
            {
                var row = table + 4 + (index * rowSize);
                var networkOrder = (ushort)Marshal.ReadInt32(row + portOffset);
                var port = (ushort)IPAddress.NetworkToHostOrder((short)networkOrder);
                listeners.TryAdd(port, Marshal.ReadInt32(row + ownerOffset));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }
}
