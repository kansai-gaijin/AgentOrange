using System.IO;

namespace AgentOrange;

/// <summary>
/// What Agent Orange starts Blender with (blender --python), so agents can use it without an add-on
/// to install or set going: it answers on Blender's MCP port in the community Blender MCP add-on's
/// own form, a JSON command and a JSON answer, and runs each command on Blender's main thread, as
/// Blender needs. Commands: get_scene_info, and execute_code, which runs Python with bpy and gives
/// back what it printed.
/// </summary>
internal static class BlenderBridge
{
    public static string Write(string agentFolder, string address)
    {
        var (host, port) = address.Split(':') is [var h, var p] && int.TryParse(p, out var number) ? (h, number) : ("127.0.0.1", 9876);
        var folder = Path.Combine(agentFolder, "blender");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "agent_orange_bridge.py");
        File.WriteAllText(path, Script.Replace("{HOST}", host).Replace("{PORT}", port.ToString()));
        return path;
    }

    private const string Script = """
        # Agent Orange's bridge to Blender, started with "blender --python" by Agent Orange.
        import bpy, contextlib, io, json, queue, socket, threading, traceback

        HOST, PORT = "{HOST}", {PORT}
        jobs = queue.Queue()

        def scene_info():
            scene = bpy.context.scene
            return {
                "name": scene.name,
                "file": bpy.data.filepath,
                "object_count": len(scene.objects),
                "objects": [{"name": o.name, "type": o.type, "location": [round(v, 4) for v in o.location]} for o in list(scene.objects)[:100]],
                "materials": [m.name for m in bpy.data.materials][:100],
            }

        # The first 3D viewport, as it looks now, as a PNG.
        def viewport_screenshot():
            import base64, os, tempfile
            path = os.path.join(tempfile.gettempdir(), "agent_orange_viewport.png")
            for window in bpy.context.window_manager.windows:
                for area in window.screen.areas:
                    if area.type == "VIEW_3D":
                        with bpy.context.temp_override(window=window, area=area):
                            bpy.ops.screen.screenshot_area(filepath=path)
                        with open(path, "rb") as shot:
                            return {"png_base64": base64.b64encode(shot.read()).decode("ascii")}
            raise ValueError("No 3D viewport is open in Blender.")

        def run(command):
            kind, params = command.get("type"), command.get("params") or {}
            if kind == "get_scene_info":
                return scene_info()
            if kind == "get_viewport_screenshot":
                return viewport_screenshot()
            if kind == "execute_code":
                printed = io.StringIO()
                with contextlib.redirect_stdout(printed):
                    exec(params.get("code", ""), {"bpy": bpy, "__name__": "__agent__"})
                return {"executed": True, "result": printed.getvalue()}
            raise ValueError("Agent Orange's bridge doesn't know " + str(kind))

        # On Blender's main thread, as bpy has to be used from there.
        def work():
            while not jobs.empty():
                command, answer, done = jobs.get()
                try:
                    answer["value"] = {"status": "success", "result": run(command)}
                except Exception as error:
                    answer["value"] = {"status": "error", "message": "".join(traceback.format_exception(error))}
                done.set()
            return 0.05

        def serve(connection):
            with connection:
                received = b""
                while True:
                    data = connection.recv(65536)
                    if not data:
                        return
                    received += data
                    try:
                        command = json.loads(received.decode("utf-8"))
                    except ValueError:
                        continue
                    received = b""
                    answer, done = {}, threading.Event()
                    jobs.put((command, answer, done))
                    done.wait(600)
                    connection.sendall(json.dumps(answer.get("value", {"status": "error", "message": "Blender took too long."})).encode("utf-8"))

        def listen():
            server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind((HOST, PORT))
            server.listen()
            while True:
                connection, _ = server.accept()
                threading.Thread(target=serve, args=(connection,), daemon=True).start()

        threading.Thread(target=listen, daemon=True).start()
        bpy.app.timers.register(work, persistent=True)
        print("Agent Orange's bridge is listening on", HOST, PORT)
        """;
}
