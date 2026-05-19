using System.Text.Json;
using System.Text.Json.Nodes;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Serialization;
using VideoEmpty.Rendering;

// A minimal MCP-style JSON-RPC stdio server exposing IVideoEmptyApi as tools.
// Reads Content-Length-framed messages on stdin and replies on stdout.
//
// Tools exposed (tools/call):
//   create_project, open_project, save_project, set_video, replace_video,
//   list_templates, create_template, update_template, delete_template,
//   list_instances, add_instance, update_instance, delete_instance,
//   start_export, get_job_status, cancel_job

var api = VideoEmptyServices.CreateApi();
var projects = new Dictionary<string, Project>();
var jsonOpts = ProjectJson.Options;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    VideoEmpty.Core.Diagnostics.Log.Error("MCP", "Unhandled exception", e.ExceptionObject as Exception);
VideoEmpty.Core.Diagnostics.Log.Info("MCP", $"Started. Log: {VideoEmpty.Core.Diagnostics.Log.LogPath}");

string ReadFrame(StreamReader stdin)
{
    int contentLength = 0;
    string? line;
    while ((line = stdin.ReadLine()) != null)
    {
        if (line.Length == 0) break;
        var idx = line.IndexOf(':');
        if (idx < 0) continue;
        var name = line.Substring(0, idx).Trim();
        var value = line.Substring(idx + 1).Trim();
        if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            contentLength = int.Parse(value);
    }
    if (contentLength == 0) return "";
    var buf = new char[contentLength];
    int read = 0;
    while (read < contentLength)
    {
        int n = stdin.Read(buf, read, contentLength - read);
        if (n <= 0) break;
        read += n;
    }
    return new string(buf, 0, read);
}

void WriteFrame(Stream stdout, string body)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
    var header = $"Content-Length: {bytes.Length}\r\n\r\n";
    var hbytes = System.Text.Encoding.UTF8.GetBytes(header);
    stdout.Write(hbytes, 0, hbytes.Length);
    stdout.Write(bytes, 0, bytes.Length);
    stdout.Flush();
}

JsonNode? Invoke(string name, JsonNode? args)
{
    Project ProjFromArgs() {
        var pid = args!["projectId"]!.GetValue<string>();
        if (!projects.TryGetValue(pid, out var p))
            throw new KeyNotFoundException($"Unknown projectId '{pid}'.");
        return p;
    }
    JsonNode Wrap(object? value) => JsonSerializer.SerializeToNode(value, jsonOpts) ?? JsonValue.Create<string?>(null)!;

    switch (name)
    {
        case "create_project":
        {
            var p = api.CreateProject(args!["name"]!.GetValue<string>());
            var id = Guid.NewGuid().ToString("n");
            projects[id] = p;
            return Wrap(new { projectId = id, project = p });
        }
        case "open_project":
        {
            var p = api.OpenProject(args!["path"]!.GetValue<string>());
            var id = Guid.NewGuid().ToString("n");
            projects[id] = p;
            return Wrap(new { projectId = id, project = p });
        }
        case "save_project":
        {
            api.SaveProject(ProjFromArgs(), args!["path"]!.GetValue<string>());
            return Wrap(new { ok = true });
        }
        case "set_video":
        {
            var p = ProjFromArgs();
            p = api.SetVideoAsync(p, args!["path"]!.GetValue<string>()).GetAwaiter().GetResult();
            projects[args!["projectId"]!.GetValue<string>()] = p;
            return Wrap(p);
        }
        case "replace_video":
        {
            var p = ProjFromArgs();
            var shift = args!["shiftMs"]?.GetValue<int>() ?? 0;
            p = api.ReplaceVideoAsync(p, args!["path"]!.GetValue<string>(), shift).GetAwaiter().GetResult();
            projects[args!["projectId"]!.GetValue<string>()] = p;
            return Wrap(p);
        }
        case "list_templates":  return Wrap(api.ListTemplates(ProjFromArgs()));
        case "list_instances":  return Wrap(api.ListInstances(ProjFromArgs()));
        case "create_template":
        {
            var t = JsonSerializer.Deserialize<Template>(args!["template"]!.ToJsonString(), jsonOpts)!;
            return Wrap(api.CreateTemplate(ProjFromArgs(), t));
        }
        case "update_template":
        {
            var t = JsonSerializer.Deserialize<Template>(args!["template"]!.ToJsonString(), jsonOpts)!;
            return Wrap(api.UpdateTemplate(ProjFromArgs(), t));
        }
        case "delete_template":
            api.DeleteTemplate(ProjFromArgs(), args!["templateId"]!.GetValue<string>());
            return Wrap(new { ok = true });
        case "add_instance":
        {
            var req = JsonSerializer.Deserialize<AddInstanceRequest>(args!["request"]!.ToJsonString(), jsonOpts)!;
            return Wrap(api.AddInstance(ProjFromArgs(), req));
        }
        case "update_instance":
        {
            var req = JsonSerializer.Deserialize<UpdateInstanceRequest>(args!["request"]!.ToJsonString(), jsonOpts)!;
            return Wrap(api.UpdateInstance(ProjFromArgs(), req));
        }
        case "delete_instance":
            api.DeleteInstance(ProjFromArgs(), args!["instanceId"]!.GetValue<string>());
            return Wrap(new { ok = true });
        case "shift_instance_times":
        {
            var req = JsonSerializer.Deserialize<ShiftInstancesRequest>(args!["request"]!.ToJsonString(), jsonOpts)!;
            return Wrap(api.ShiftInstanceTimes(ProjFromArgs(), req));
        }
        case "start_export":
        {
            var opts = JsonSerializer.Deserialize<ExportOptions>(args!["options"]!.ToJsonString(), jsonOpts)!;
            return Wrap(new { jobId = api.StartExport(ProjFromArgs(), opts) });
        }
        case "get_job_status":
            return Wrap(api.GetJobStatus(args!["jobId"]!.GetValue<string>()));
        case "cancel_job":
            api.CancelJob(args!["jobId"]!.GetValue<string>());
            return Wrap(new { ok = true });
        case "check_dependencies":
            return Wrap(api.Dependencies.CheckAsync().GetAwaiter().GetResult());
        case "install_dependencies":
            api.Dependencies.InstallMissingAsync().GetAwaiter().GetResult();
            return Wrap(api.Dependencies.CheckAsync().GetAwaiter().GetResult());
        default:
            throw new InvalidOperationException($"Unknown tool '{name}'.");
    }
}

var stdin = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
var stdout = Console.OpenStandardOutput();

while (true)
{
    string body;
    try { body = ReadFrame(stdin); }
    catch { break; }
    if (string.IsNullOrEmpty(body)) break;

    JsonNode? msg;
    try { msg = JsonNode.Parse(body); }
    catch { continue; }
    if (msg is null) continue;

    var id = msg["id"];
    var method = msg["method"]?.GetValue<string>();
    var prms = msg["params"];

    var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone() };
    try
    {
        switch (method)
        {
            case "initialize":
                response["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "VideoEmpty", ["version"] = "0.1.0" }
                };
                break;
            case "tools/list":
                response["result"] = new JsonObject { ["tools"] = ToolList() };
                break;
            case "tools/call":
            {
                var nameArg = prms!["name"]!.GetValue<string>();
                var callArgs = prms["arguments"];
                var result = Invoke(nameArg, callArgs);
                response["result"] = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = result?.ToJsonString() ?? "null" }
                    }
                };
                break;
            }
            default:
                response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method '{method}' not found." };
                break;
        }
    }
    catch (Exception ex)
    {
        response["error"] = new JsonObject { ["code"] = -32000, ["message"] = ex.Message };
    }

    if (id is not null)
        WriteFrame(stdout, response.ToJsonString());
}

static JsonArray ToolList()
{
    JsonObject Tool(string name, string desc) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["inputSchema"] = new JsonObject { ["type"] = "object" }
    };
    return new JsonArray
    {
        Tool("create_project", "Create a new VideoEmpty project."),
        Tool("open_project", "Open a .veproj project file."),
        Tool("save_project", "Save the project to a .veproj file."),
        Tool("set_video", "Attach a source video to the project."),
        Tool("replace_video", "Replace the source video and shift all instances by shiftMs (positive=later, negative=earlier)."),
        Tool("list_templates", "List templates in a project."),
        Tool("create_template", "Add a new template."),
        Tool("update_template", "Update a template."),
        Tool("delete_template", "Delete a template."),
        Tool("list_instances", "List template instances on the timeline."),
        Tool("add_instance", "Place a template on the video at (centerX, centerY, startMs)."),
        Tool("update_instance", "Update an existing instance."),
        Tool("delete_instance", "Remove an instance from the timeline."),
        Tool("shift_instance_times", "Bulk-shift StartMs of instances; request: { shiftMs, scope (All|Before|After|AtOrBefore|AtOrAfter|OnlyReference), referenceInstanceId? }."),
        Tool("start_export", "Start an export job; returns jobId."),
        Tool("get_job_status", "Get the status/progress of an export job."),
        Tool("cancel_job", "Cancel a running export job."),
        Tool("check_dependencies", "Check whether ffmpeg/ffprobe are installed."),
        Tool("install_dependencies", "Install missing ffmpeg via the OS package manager (winget / brew).")
    };
}
