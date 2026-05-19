using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Serialization;
using VideoEmpty.Rendering;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(VideoEmptyServices.CreateApi());
builder.Services.AddSingleton<ProjectStore>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var c in ProjectJson.Options.Converters)
        o.SerializerOptions.Converters.Add(c);
    o.SerializerOptions.PropertyNamingPolicy = ProjectJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = ProjectJson.Options.DefaultIgnoreCondition;
    o.SerializerOptions.WriteIndented = false;
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "VideoEmpty" }));

// ---- Projects ----
app.MapPost("/projects", (CreateProjectRequest req, IVideoEmptyApi api, ProjectStore store) =>
{
    var p = api.CreateProject(req.Name);
    var id = store.Put(p);
    return Results.Ok(new { projectId = id, project = p });
});

app.MapGet("/projects/{id}", (string id, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(p) : Results.NotFound());

app.MapPost("/projects/{id}/save", (string id, SavePathRequest req, IVideoEmptyApi api, ProjectStore store) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    api.SaveProject(p, req.Path);
    return Results.Ok();
});

app.MapPost("/projects/open", (SavePathRequest req, IVideoEmptyApi api, ProjectStore store) =>
{
    var p = api.OpenProject(req.Path);
    var id = store.Put(p);
    return Results.Ok(new { projectId = id, project = p });
});

app.MapPost("/projects/{id}/video", async (string id, SetVideoRequest req, IVideoEmptyApi api, ProjectStore store, CancellationToken ct) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    p = await api.SetVideoAsync(p, req.Path, ct);
    store.Put(id, p);
    return Results.Ok(p);
});

app.MapPost("/projects/{id}/video/replace", async (string id, ReplaceVideoRequest req, IVideoEmptyApi api, ProjectStore store, CancellationToken ct) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    p = await api.ReplaceVideoAsync(p, req.Path, req.ShiftMs, ct);
    store.Put(id, p);
    return Results.Ok(p);
});

// ---- Templates ----
app.MapGet("/projects/{id}/templates", (string id, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.ListTemplates(p)) : Results.NotFound());

app.MapPost("/projects/{id}/templates", (string id, Template t, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.CreateTemplate(p, t)) : Results.NotFound());

app.MapPut("/projects/{id}/templates", (string id, Template t, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.UpdateTemplate(p, t)) : Results.NotFound());

app.MapDelete("/projects/{id}/templates/{templateId}", (string id, string templateId, IVideoEmptyApi api, ProjectStore store) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    api.DeleteTemplate(p, templateId);
    return Results.Ok();
});

// ---- Instances ----
app.MapGet("/projects/{id}/instances", (string id, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.ListInstances(p)) : Results.NotFound());

app.MapPost("/projects/{id}/instances", (string id, AddInstanceRequest req, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.AddInstance(p, req)) : Results.NotFound());

app.MapPut("/projects/{id}/instances", (string id, UpdateInstanceRequest req, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.UpdateInstance(p, req)) : Results.NotFound());

app.MapDelete("/projects/{id}/instances/{instanceId}", (string id, string instanceId, IVideoEmptyApi api, ProjectStore store) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    api.DeleteInstance(p, instanceId);
    return Results.Ok();
});

app.MapPost("/projects/{id}/instances/shift", (string id, ShiftInstancesRequest req, IVideoEmptyApi api, ProjectStore store) =>
    store.TryGet(id, out var p) ? Results.Ok(api.ShiftInstanceTimes(p, req)) : Results.NotFound());

// ---- Preview ----
app.MapGet("/projects/{id}/preview/{timeMs:int}", async (string id, int timeMs, IVideoEmptyApi api, ProjectStore store, CancellationToken ct) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    var bytes = await api.RenderFrameAsync(p, timeMs, ct);
    return Results.File(bytes, "image/png");
});

// ---- Export ----
app.MapPost("/projects/{id}/export", (string id, ExportOptions options, IVideoEmptyApi api, ProjectStore store) =>
{
    if (!store.TryGet(id, out var p)) return Results.NotFound();
    var jobId = api.StartExport(p, options);
    return Results.Ok(new { jobId });
});

app.MapGet("/jobs/{jobId}", (string jobId, IVideoEmptyApi api) => Results.Ok(api.GetJobStatus(jobId)));
app.MapPost("/jobs/{jobId}/cancel", (string jobId, IVideoEmptyApi api) => { api.CancelJob(jobId); return Results.Ok(); });

app.MapGet("/dependencies", async (IVideoEmptyApi api, CancellationToken ct) =>
    Results.Ok(await api.Dependencies.CheckAsync(ct)));
app.MapPost("/dependencies/install", async (IVideoEmptyApi api, CancellationToken ct) =>
{
    await api.Dependencies.InstallMissingAsync(null, ct);
    return Results.Ok(await api.Dependencies.CheckAsync(ct));
});

app.Run();

public sealed record CreateProjectRequest(string Name);
public sealed record SavePathRequest(string Path);
public sealed record SetVideoRequest(string Path);
public sealed record ReplaceVideoRequest(string Path, int ShiftMs);

/// <summary>Simple in-memory project store keyed by id.</summary>
public sealed class ProjectStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Project> _store = new();
    public string Put(Project p)
    {
        var id = Guid.NewGuid().ToString("n");
        _store[id] = p;
        return id;
    }
    public void Put(string id, Project p) => _store[id] = p;
    public bool TryGet(string id, out Project p) => _store.TryGetValue(id, out p!);
}
