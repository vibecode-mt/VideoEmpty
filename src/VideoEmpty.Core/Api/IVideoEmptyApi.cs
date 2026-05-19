using System.Collections.Generic;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Core.Api;

public sealed record AddInstanceRequest(
    string TemplateId,
    double CenterX,
    double CenterY,
    int StartMs,
    int? DurationMs = null,
    Dictionary<string, string>? TextValues = null,
    Animation? AnimationOverride = null);

public sealed record UpdateInstanceRequest(
    string InstanceId,
    double? CenterX = null,
    double? CenterY = null,
    int? StartMs = null,
    int? DurationMs = null,
    Dictionary<string, string>? TextValues = null,
    Animation? AnimationOverride = null);

public sealed record VideoInfo(int Width, int Height, double Fps, int DurationMs);

public sealed record ExportOptions(
    string OutputPath,
    string VideoCodec = "libx264",
    string AudioCodec = "aac",
    int? VideoBitrateKbps = null,
    int? Crf = 18,
    bool UseHardwareAcceleration = true,
    string? Preset = null);

public sealed record ExportSubtitlesOptions(
    string OutputPath,
    string Format = "srt", // "srt", "vtt", "json"
    string? TemplateTypeFilter = null, // legacy: single template name substring (null = all)
    int? StartTimeMs = null,
    int? EndTimeMs = null,
    IReadOnlyList<string>? TemplateNameFilters = null); // null/empty = all; otherwise instance template name must match one of these (exact match)

public sealed record SubtitleEntry(
    int IndexOrId,
    int StartMs,
    int EndMs,
    string Text,
    string TemplateName,
    double? CenterX,
    double? CenterY);

public enum JobState { Pending, Running, Completed, Failed, Cancelled }

public sealed class JobStatus
{
    public string JobId { get; set; } = "";
    public JobState State { get; set; }
    public double Progress { get; set; } // 0..1
    public string? Message { get; set; }
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Scope selector for <see cref="IVideoEmptyApi.ShiftInstanceTimes"/>.
/// All comparisons are against the reference instance's current <c>StartMs</c>.
/// </summary>
public enum InstanceShiftScope
{
    /// <summary>Every instance in the project.</summary>
    All,
    /// <summary>Instances with <c>StartMs &lt; reference.StartMs</c>.</summary>
    Before,
    /// <summary>Instances with <c>StartMs &gt; reference.StartMs</c>.</summary>
    After,
    /// <summary>Instances with <c>StartMs &lt;= reference.StartMs</c> (includes the reference itself).</summary>
    AtOrBefore,
    /// <summary>Instances with <c>StartMs &gt;= reference.StartMs</c> (includes the reference itself).</summary>
    AtOrAfter,
    /// <summary>Only the reference instance.</summary>
    OnlyReference
}

public sealed record ShiftInstancesRequest(
    int ShiftMs,
    InstanceShiftScope Scope = InstanceShiftScope.All,
    string? ReferenceInstanceId = null);

public sealed record ShiftInstancesResult(int ShiftedCount, int ClampedToZeroCount);

/// <summary>
/// Unified API surface for VideoEmpty. Implemented in-process; UI calls it directly,
/// HTTP server and MCP server are thin adapters over this interface.
/// </summary>
public interface IVideoEmptyApi
{
    // Project
    Project CreateProject(string name);
    Project OpenProject(string path);
    void SaveProject(Project project, string path);
    Task<Project> SetVideoAsync(Project project, string videoPath, CancellationToken ct = default);

    /// <summary>
    /// Replaces the project's video file and shifts every template instance's start time
    /// by <paramref name="shiftMs"/> milliseconds (positive = later, negative = earlier).
    /// Instances that would end up before 0ms are clamped to 0. Use this when swapping
    /// in a new recording whose timeline is offset from the original (e.g. an extra
    /// intro clip in front).
    /// </summary>
    Task<Project> ReplaceVideoAsync(Project project, string videoPath, int shiftMs, CancellationToken ct = default);

    /// <summary>Probes a video file for resolution, fps and duration without modifying any project.</summary>
    Task<VideoInfo> ProbeVideoAsync(string videoPath, CancellationToken ct = default);

    // Templates
    IReadOnlyList<Template> ListTemplates(Project project);
    Template GetTemplate(Project project, string templateId);
    Template CreateTemplate(Project project, Template template);
    Template UpdateTemplate(Project project, Template template);
    void DeleteTemplate(Project project, string templateId);
    Template DuplicateTemplate(Project project, string templateId, string? newName = null);

    // Instances
    TemplateInstance AddInstance(Project project, AddInstanceRequest request);
    TemplateInstance UpdateInstance(Project project, UpdateInstanceRequest request);
    void DeleteInstance(Project project, string instanceId);
    IReadOnlyList<TemplateInstance> ListInstances(Project project);

    /// <summary>
    /// Bulk-shifts the <c>StartMs</c> of template instances by <paramref name="request"/>.ShiftMs
    /// (positive = later, negative = earlier). The <see cref="ShiftInstancesRequest.Scope"/>
    /// selects which instances are affected; for non-<see cref="InstanceShiftScope.All"/> scopes,
    /// <see cref="ShiftInstancesRequest.ReferenceInstanceId"/> must be supplied.
    /// Resulting times are clamped to 0; the result reports how many instances were touched
    /// and how many were clamped (so the UI can warn about data loss).
    /// </summary>
    ShiftInstancesResult ShiftInstanceTimes(Project project, ShiftInstancesRequest request);

    // Preview
    Task<byte[]> RenderFrameAsync(Project project, int timeMs, CancellationToken ct = default);
    byte[] RenderTemplatePreview(Template template, IReadOnlyDictionary<string, string>? textValues = null);

    /// <summary>
    /// Streams composed preview frames (overlays drawn) starting at <paramref name="startMs"/>
    /// at the requested <paramref name="fps"/>. The producer pushes frames as fast as they decode;
    /// the consumer is responsible for pacing/discarding to match wall-clock playback.
    /// Each yielded frame is a JPEG byte array tagged with its logical playback timestamp.
    /// </summary>
    IAsyncEnumerable<FrameStreamItem> StreamPreviewFramesAsync(
        Project project, int startMs, double fps, int maxWidth, CancellationToken ct = default);

    // Export
    string StartExport(Project project, ExportOptions options);
    Task ExportSubtitlesAsync(Project project, ExportSubtitlesOptions options, CancellationToken ct = default);
    /// <summary>Append our template instances into an existing CapCut PC project folder.</summary>
    CapCutExportResult ExportToCapCut(Project project, CapCutExportOptions options);
    JobStatus GetJobStatus(string jobId);
    void CancelJob(string jobId);

    // Dependencies
    IDependencyManager Dependencies { get; }
}
