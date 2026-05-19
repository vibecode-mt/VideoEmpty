using VideoEmpty.Core.Api;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Templates;
using Xunit;

namespace VideoEmpty.Core.Tests;

public class VideoEmptyApiTests
{
    private static IVideoEmptyApi NewApi() =>
        new VideoEmptyApi(new FakeRenderer(), new FakeProbe(), new FakePreview(), new FakeExporter(), new FakeDeps());

    [Fact]
    public void CreateProject_SeedsBuiltInTemplates()
    {
        var api = NewApi();
        var p = api.CreateProject("p1");
        Assert.Equal(2, p.Templates.Count);
        Assert.Contains(p.Templates, t => t.Id == BuiltInTemplates.StepTemplateId);
        Assert.Contains(p.Templates, t => t.Id == BuiltInTemplates.CommentTemplateId);
    }

    [Fact]
    public void AddInstance_UsesTemplateDefaultDuration_AndClampsCenter()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        var inst = api.AddInstance(p, new AddInstanceRequest(
            BuiltInTemplates.StepTemplateId, 1.5, -0.2, 500));
        Assert.Equal(3000, inst.DurationMs);
        Assert.Equal(1.0, inst.Center.X);
        Assert.Equal(0.0, inst.Center.Y);
        Assert.Single(p.Instances);
    }

    [Fact]
    public void UpdateInstance_PartialFields()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        var inst = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 0));
        api.UpdateInstance(p, new UpdateInstanceRequest(inst.Id, DurationMs: 5000,
            TextValues: new() { [BuiltInTemplates.StepNumberElementId] = "2." }));
        Assert.Equal(5000, p.Instances[0].DurationMs);
        Assert.Equal("2.", p.Instances[0].TextValues[BuiltInTemplates.StepNumberElementId]);
    }

    [Fact]
    public void DeleteTemplate_RejectedIfInUse()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 0));
        Assert.Throws<InvalidOperationException>(() => api.DeleteTemplate(p, BuiltInTemplates.StepTemplateId));
    }

    [Fact]
    public async Task ReplaceVideoAsync_ShiftsInstancesLater_AndClampsBelowZero()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        await api.SetVideoAsync(p, "old.mp4");
        var a = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 1_000));
        var b = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 5_000));

        await api.ReplaceVideoAsync(p, "new.mp4", shiftMs: 10_000);
        Assert.Equal("new.mp4", p.VideoPath);
        Assert.Equal(11_000, p.Instances.Single(i => i.Id == a.Id).StartMs);
        Assert.Equal(15_000, p.Instances.Single(i => i.Id == b.Id).StartMs);

        await api.ReplaceVideoAsync(p, "new.mp4", shiftMs: -20_000);
        Assert.Equal(0, p.Instances.Single(i => i.Id == a.Id).StartMs);
        Assert.Equal(0, p.Instances.Single(i => i.Id == b.Id).StartMs);
    }

    [Fact]
    public async Task ReplaceVideoAsync_ZeroShift_LeavesInstancesUnchanged()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        await api.SetVideoAsync(p, "old.mp4");
        var a = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 2_500));

        await api.ReplaceVideoAsync(p, "new.mp4", shiftMs: 0);
        Assert.Equal(2_500, p.Instances.Single(i => i.Id == a.Id).StartMs);
    }

    [Fact]
    public void ShiftInstanceTimes_All_AppliesToEveryInstance_AndReportsClamps()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        var a = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 1_000));
        var b = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 5_000));

        var r = api.ShiftInstanceTimes(p, new ShiftInstancesRequest(-2_000, InstanceShiftScope.All));
        Assert.Equal(2, r.ShiftedCount);
        Assert.Equal(1, r.ClampedToZeroCount); // a (1000 - 2000 -> 0)
        Assert.Equal(0,     p.Instances.Single(i => i.Id == a.Id).StartMs);
        Assert.Equal(3_000, p.Instances.Single(i => i.Id == b.Id).StartMs);
    }

    [Theory]
    [InlineData(InstanceShiftScope.Before,      new[] { 1_000 })]                  // strictly < 3000
    [InlineData(InstanceShiftScope.AtOrBefore,  new[] { 1_000, 3_000 })]
    [InlineData(InstanceShiftScope.After,       new[] { 5_000 })]                  // strictly > 3000
    [InlineData(InstanceShiftScope.AtOrAfter,   new[] { 3_000, 5_000 })]
    [InlineData(InstanceShiftScope.OnlyReference, new[] { 3_000 })]
    public void ShiftInstanceTimes_RelativeScopes_AffectExpectedInstances(InstanceShiftScope scope, int[] expectedShiftedStarts)
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        var a = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 1_000));
        var refInst = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 3_000));
        var c = api.AddInstance(p, new AddInstanceRequest(BuiltInTemplates.StepTemplateId, 0.5, 0.5, 5_000));
        var originalsById = p.Instances.ToDictionary(i => i.Id, i => i.StartMs);

        var r = api.ShiftInstanceTimes(p, new ShiftInstancesRequest(+10_000, scope, refInst.Id));
        Assert.Equal(expectedShiftedStarts.Length, r.ShiftedCount);

        foreach (var inst in p.Instances)
        {
            var wasShifted = expectedShiftedStarts.Contains(originalsById[inst.Id]);
            var expected = wasShifted ? originalsById[inst.Id] + 10_000 : originalsById[inst.Id];
            Assert.Equal(expected, inst.StartMs);
        }
    }

    [Fact]
    public void ShiftInstanceTimes_NonAllScope_RequiresReferenceId()
    {
        var api = NewApi();
        var p = api.CreateProject("p");
        Assert.Throws<ArgumentException>(() =>
            api.ShiftInstanceTimes(p, new ShiftInstancesRequest(1_000, InstanceShiftScope.After)));
    }

    private sealed class FakeRenderer : ITemplateRenderer
    { public byte[] RenderTemplatePng(Template t, IReadOnlyDictionary<string,string>? v=null) => Array.Empty<byte>(); }
    private sealed class FakeProbe : IVideoProbe
    { public Task<VideoInfo> ProbeAsync(string p, CancellationToken ct=default) => Task.FromResult(new VideoInfo(1920,1080,30,10000)); }
    private sealed class FakePreview : IFramePreview
    { 
        public Task<byte[]> ExtractFrameAsync(string p, int t, CancellationToken ct=default) => Task.FromResult(Array.Empty<byte>());
        public async IAsyncEnumerable<FrameStreamItem> StreamFramesAsync(string p, int s, double f, int w, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct=default)
        { await Task.CompletedTask; yield break; }
    }
    private sealed class FakeExporter : IVideoExporter
    {
        public string Start(Project p, ExportOptions o) => "job1";
        public JobStatus GetStatus(string id) => new() { JobId = id, State = JobState.Completed };
        public void Cancel(string id) { }
    }
    private sealed class FakeDeps : IDependencyManager
    {
        public bool HasMissing => false;
        public Task<IReadOnlyList<DependencyStatus>> CheckAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DependencyStatus>>(Array.Empty<DependencyStatus>());
        public Task InstallMissingAsync(IProgress<DependencyInstallProgress>? p = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
