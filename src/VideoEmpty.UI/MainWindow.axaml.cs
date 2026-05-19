using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EllipseShape = Avalonia.Controls.Shapes.Ellipse;
using LineShape = Avalonia.Controls.Shapes.Line;
using RectangleShape = Avalonia.Controls.Shapes.Rectangle;
using AvaloniaMedia = Avalonia.Media;
using VideoEmpty.Core.Api;
using VideoEmpty.Core.Diagnostics;
using VideoEmpty.Core.Model;
using VideoEmpty.Core.Serialization;
using VideoEmpty.Rendering;

namespace VideoEmpty.UI;

public partial class MainWindow : Window
{
    private readonly IVideoEmptyApi _api = VideoEmptyServices.CreateApi();
    private readonly UiSettings _settings = UiSettingsStore.Load();
    private Project _project;
    private string? _projectPath;
    private string? _armedTemplateId;
    private int _currentTimeMs;
    private bool _isApplyingProject;
    private (double x, double y)? _previewHoverNormalized;
    private int _previewRenderBusy;
    private int _previewRenderPending;
    private int _previewRenderRequestedTimeMs;
    private readonly Dictionary<int, byte[]> _playbackFrameCache = new();
    private readonly Queue<int> _playbackFrameCacheOrder = new();
    private bool _dependenciesMissing;
    private const int PlaybackPreviewQuantizeMs = 100; // 10fps preview target while playing
    private const int PlaybackFrameCacheMax = 120;
    private const double PlaybackStreamFps = 15.0;
    private const int PlaybackStreamMaxWidth = 1280;
    private CancellationTokenSource? _playbackStreamCts;
    private Task? _playbackStreamTask;
    private bool _isSliderInternalUpdate;

    private bool _compactMode;
    private bool _showLeftPanel = true;
    private bool _showRightPanel = true;

    public ObservableCollection<Template> Templates { get; } = new();
    public ObservableCollection<InstanceListItem> Instances { get; } = new();
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = new();
    public ObservableCollection<ElementListItem> ElementsListItems { get; } = new();
    public ObservableCollection<InstanceTextFieldItem> InstanceTextFields { get; } = new();

    private bool _isApplyingTemplateEditor;
    private bool _isApplyingInstanceEditor;
    private string? _instanceEditorInstanceId;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        _project = _api.CreateProject("Untitled");
        DataContext = this;

        TemplatesList.ItemsSource = Templates;
        CompactTemplatesListBox.ItemsSource = Templates;
        InstancesList.ItemsSource = Instances;
        RecentProjectsList.ItemsSource = RecentProjects;
        ElementsList.ItemsSource = ElementsListItems;
        InstanceTextFieldsList.ItemsSource = InstanceTextFields;
        CompactOverlayFieldsList.ItemsSource = InstanceTextFields;
        TemplateEnterBox.ItemsSource = Enum.GetValues<AnimationStyle>();
        TemplateExitBox.ItemsSource = Enum.GetValues<AnimationStyle>();
        ShapeKindBox.ItemsSource = Enum.GetValues<ShapeKind>();
        TextHAlignBox.ItemsSource = Enum.GetValues<HorizontalAlign>();
        TextVAlignBox.ItemsSource = Enum.GetValues<VerticalAlign>();

        SaveProjectButton.Click += OnSaveProject;
        UndoButton.Click += OnUndo;
        ShiftInstancesButton.Click += OnShiftInstances;
        SettingsButton.Click += OnSettings;
        DashboardButton.Click += (_, _) => ShowDashboard();
        OpenVideoButton.Click += OnOpenVideo;
        ExportButton.Click += OnExport;
        ExportSubtitlesButton.Click += OnExportSubtitles;
        ExportCapCutButton.Click += OnExportCapCut;
        TogglePanelsButton.Click += OnTogglePanels;
        CompactModeButton.Click += OnToggleCompactMode;
        CollapseLeftButton.Click += OnCollapseLeft;
        CollapseRightButton.Click += OnCollapseRight;
        ExpandLeftEdgeButton.Click += OnExpandLeftEdge;
        ExpandRightEdgeButton.Click += OnExpandRightEdge;
        CompactOverlayCloseButton.Click += OnCompactOverlayClose;
        CompactOverlayExpandButton.Click += OnCompactOverlayExpand;
        InstallDepsButton.Click += OnInstallDeps;
        DashboardInstallDepsButton.Click += OnInstallDeps;
        OpenLogButton.Click += (_, _) => OpenInShell(Log.LogPath);

        DashboardNewProjectButton.Click += OnNewProject;
        DashboardOpenProjectButton.Click += OnOpenProject;
        DashboardOpenLogButton.Click += (_, _) => OpenInShell(Log.LogPath);
        RecentProjectsList.DoubleTapped += OnOpenRecentProject;
        AddTemplateButton.Click += OnAddTemplate;
        DuplicateTemplateButton.Click += OnDuplicateTemplate;
        DeleteTemplateButton.Click += OnDeleteTemplate;
        ApplyTemplateJsonButton.Click += OnApplyTemplateJson;
        WireTemplateEditorAutoApply();

        TimeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _currentTimeMs = (int)TimeSlider.Value;
                UpdatePlaybackTimeLabels(_currentTimeMs);
                if (_isSliderInternalUpdate) return;
                // User seek (or any non-internal change) while streaming: restart the stream
                // at the new position so playback reflects the new playhead.
                if (_playbackStreamTask is not null)
                {
                    _ = RestartPlaybackStreamAsync(_currentTimeMs);
                }
                else
                {
                    _ = RefreshPreviewAsync();
                }
            }
        };

        TemplatesList.SelectionChanged += (_, _) =>
        {
            if (TemplatesList.SelectedItem is Template t)
            {
                _armedTemplateId = t.Id;
                ArmedLabel.Text = $"Armed: {t.Name} (click to add, Shift+click to retime latest)";
            }
            else
            {
                _armedTemplateId = null;
                ArmedLabel.Text = "(none armed)";
                _previewHoverNormalized = null;
            }
            RenderPlacementOverlay();
            UpdateTemplateEditor();
        };

        PreviewImage.PointerPressed += OnPreviewClicked;
        PreviewImage.PointerMoved += OnPreviewPointerMoved;
        PreviewImage.PointerExited += OnPreviewPointerExited;
        InstancesList.SelectionChanged += (_, _) => UpdateInstanceEditor();
        DeleteInstanceButton.Click += OnDeleteInstance;
        PreviewInstanceButton.Click += OnPreviewInstance;

        PlayPauseButton.Click += (_, _) => TogglePlay();
        StepBackButton.Click += (_, _) => SeekRelative(-FrameDurationMs());
        StepForwardButton.Click += (_, _) => SeekRelative(+FrameDurationMs());
        JumpBack1sButton.Click += (_, _) => SeekRelative(-1000);
        JumpForward1sButton.Click += (_, _) => SeekRelative(+1000);
        JumpBack10sButton.Click += (_, _) => SeekRelative(-10000);
        JumpForward10sButton.Click += (_, _) => SeekRelative(+10000);

        InstanceStartBox.LostFocus += (_, _) => CommitInstanceEdit();
        InstanceDurationBox.LostFocus += (_, _) => CommitInstanceEdit();
        InstanceXBox.LostFocus += (_, _) => CommitInstanceEdit();
        InstanceYBox.LostFocus += (_, _) => CommitInstanceEdit();

        LoadRecentProjects();
        ApplyProject(_project, null, showDashboard: true);
        Dispatcher.UIThread.Post(async () => await CheckDependenciesAsync(promptIfMissing: true), DispatcherPriority.Background);
    }

    private void ApplyProject(Project project, string? path, bool showDashboard = false)
    {
        _isApplyingProject = true;
        try
        {
            _project = project;
            _projectPath = path;
            _currentTimeMs = 0;
            _previewHoverNormalized = null;
            ClearPlaybackFrameCache();
            TimeSlider.Value = 0;
            TimeSlider.Maximum = Math.Max(1, _project.VideoDurationMs);
            VideoInfoLabel.Text = string.IsNullOrWhiteSpace(_project.VideoPath)
                ? "No video loaded."
                : $"{Path.GetFileName(_project.VideoPath)} • {_project.VideoResolution.Width}x{_project.VideoResolution.Height} @ {_project.VideoFps:0.##} fps • {_project.VideoDurationMs / 1000.0:0.0}s";
            RefreshTemplates();
            RefreshInstances();
            UpdateTemplateEditor();
            UpdateInstanceEditor();
            DashboardRoot.IsVisible = showDashboard;
            EditorRoot.IsVisible = !showDashboard;
            ApplyLayoutMode();
            _ = RefreshPreviewAsync();
        }
        finally
        {
            _isApplyingProject = false;
        }
    }

    private void ShowDashboard()
    {
        LoadRecentProjects();
        DashboardRoot.IsVisible = true;
        EditorRoot.IsVisible = false;
        ApplyLayoutMode();
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var defaultName = $"{DateTime.Today:yyyy-MM-dd}-Project";
        var dlg = new TextEntryDialog("Project name", "New Project", defaultName);
        var input = await dlg.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(input)) return;

        var name = input.Trim();
        var p = _api.CreateProject(name);
        var path = UiSettingsStore.GetProjectPath(name);
        ApplyProject(p, path);
        RememberRecentProject(path);
        await AutoSaveAsync("new-project");
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("VideoEmpty Project") { Patterns = new[] { "*.veproj" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        await OpenProjectPathAsync(path);
    }

    private async void OnOpenRecentProject(object? sender, RoutedEventArgs e)
    {
        if (RecentProjectsList.SelectedItem is not RecentProjectItem item) return;
        await OpenProjectPathAsync(item.Path);
    }

    private async Task OpenProjectPathAsync(string path)
    {
        try
        {
            var p = _api.OpenProject(path);
            ApplyProject(p, path);
            RememberRecentProject(path);
            if (!string.IsNullOrWhiteSpace(_project.VideoPath)) await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", $"Open project failed: {path}", ex);
            VideoInfoLabel.Text = $"Open project failed: {ex.Message}";
        }
    }

    private async void OnSaveProject(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            DefaultExtension = "veproj",
            SuggestedFileName = (_project.Name ?? "project") + ".veproj"
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _projectPath = path;
        await AutoSaveAsync("manual-save");
    }

    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            ExportStatus.Text = "Undo unavailable: project not saved yet.";
            return;
        }

        var backupDir = UiSettingsStore.GetBackupDir(_projectPath);
        var latest = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "*.veproj.bak").OrderByDescending(x => x).FirstOrDefault()
            : null;
        if (latest is null)
        {
            ExportStatus.Text = "Undo unavailable: no backup file.";
            return;
        }

        try
        {
            File.Copy(latest, _projectPath, overwrite: true);
            var p = _api.OpenProject(_projectPath);
            ApplyProject(p, _projectPath);
            ExportStatus.Text = $"Undo restored backup: {Path.GetFileName(latest)}";
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Undo restore failed", ex);
            ExportStatus.Text = $"Undo failed: {ex.Message}";
        }
    }

    private async void OnShiftInstances(object? sender, RoutedEventArgs e)
    {
        if (_project.Instances.Count == 0)
        {
            ExportStatus.Text = "Shift Times: no instances to shift.";
            return;
        }

        var sel = SelectedInstance;
        string? refTemplateName = null;
        if (sel is not null)
        {
            var tpl = _project.Templates.FirstOrDefault(t => t.Id == sel.TemplateId);
            refTemplateName = tpl?.Name ?? sel.TemplateId;
        }

        var result = await ShiftInstancesDialog.ShowAsync(this, sel, refTemplateName);
        if (result is null) return;

        try
        {
            var (shiftMs, scope) = result.Value;
            var outcome = _api.ShiftInstanceTimes(_project, new ShiftInstancesRequest(
                shiftMs, scope, sel?.Id));
            ExportStatus.Text = outcome.ClampedToZeroCount == 0
                ? $"Shifted {outcome.ShiftedCount} instance(s) by {Timecode.Format(shiftMs)}."
                : $"Shifted {outcome.ShiftedCount} instance(s) by {Timecode.Format(shiftMs)} ({outcome.ClampedToZeroCount} clamped to 0).";
            RefreshInstances();
            await RefreshPreviewAsync();
            await AutoSaveAsync("shift-instance-times");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "ShiftInstances failed", ex);
            ExportStatus.Text = $"Shift failed: {ex.Message}";
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_settings);
        var ok = await dlg.ShowDialog<bool>(this);
        if (!ok) return;
        _settings.AutoDeleteBackupsEnabled = dlg.EnableAutoDelete == true;
        _settings.AutoDeleteBackupsDays = Math.Max(1, dlg.AutoDeleteDays ?? 90);
        _settings.SnapToGridEnabled = dlg.SnapToGridEnabled == true;
        _settings.SnapGridDivisions = Math.Max(2, dlg.SnapGridDivisions ?? 10);
        _settings.CapCutProjectsFolder = dlg.CapCutProjectsFolder ?? "";
        UiSettingsStore.Save(_settings);
        CleanupBackups();
        RenderPlacementOverlay();
        ExportStatus.Text = "Settings saved.";
    }

    private int FrameDurationMs()
    {
        var fps = _project.VideoFps > 0 ? _project.VideoFps : 30.0;
        return Math.Max(1, (int)Math.Round(1000.0 / fps));
    }

    private void TogglePlay()
    {
        if (_project.VideoDurationMs <= 0) return;
        if (_playbackStreamTask is not null)
        {
            StopPlaybackStream();
            PlayPauseButton.Content = "▶ Play";
            return;
        }
        StartPlaybackStream(_currentTimeMs);
        PlayPauseButton.Content = "⏸ Pause";
    }

    private void StartPlaybackStream(int startMs)
    {
        StopPlaybackStream();
        ClearPlaybackFrameCache();
        var cts = new CancellationTokenSource();
        _playbackStreamCts = cts;
        _playbackStreamTask = Task.Run(() => RunPlaybackStreamAsync(startMs, cts.Token));
    }

    private void StopPlaybackStream()
    {
        var cts = _playbackStreamCts;
        _playbackStreamCts = null;
        _playbackStreamTask = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
    }

    private async Task RestartPlaybackStreamAsync(int startMs)
    {
        if (_playbackStreamTask is null) return;
        var old = _playbackStreamTask;
        var oldCts = _playbackStreamCts;
        _playbackStreamCts = null;
        _playbackStreamTask = null;
        try { oldCts?.Cancel(); } catch { /* ignore */ }
        try { if (old is not null) await old.ConfigureAwait(true); } catch { /* ignore */ }
        try { oldCts?.Dispose(); } catch { /* ignore */ }
        if (PlayPauseButton.Content as string == "⏸ Pause")
            StartPlaybackStream(startMs);
    }

    private async Task RunPlaybackStreamAsync(int startMs, CancellationToken ct)
    {
        try
        {
            var fps = PlaybackStreamFps;
            var frameMs = 1000.0 / fps;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int idx = 0;
            await foreach (var frame in _api.StreamPreviewFramesAsync(
                _project, startMs, fps, PlaybackStreamMaxWidth, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                if (frame.TimeMs >= _project.VideoDurationMs)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (LoopCheck.IsChecked == true)
                        {
                            _ = RestartPlaybackStreamAsync(0);
                        }
                        else
                        {
                            StopPlaybackStream();
                            PlayPauseButton.Content = "▶ Play";
                            SetSliderInternal(_project.VideoDurationMs);
                        }
                    });
                    break;
                }

                // Pace to real wall-clock: each frame target is idx * frameMs after start.
                var target = (long)(idx * frameMs);
                var delay = target - sw.ElapsedMilliseconds;
                if (delay > 1)
                {
                    try { await Task.Delay((int)delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                else if (delay < -250 && idx > 0)
                {
                    // Falling behind by >250ms: drop this frame to catch up.
                    idx++;
                    continue;
                }

                var jpeg = frame.Jpeg;
                var timeMs = frame.TimeMs;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(jpeg);
                        PreviewImage.Source = new Bitmap(ms);
                    }
                    catch { /* ignore decode failures */ }
                    _currentTimeMs = timeMs;
                    SetSliderInternal(timeMs);
                    UpdatePlaybackTimeLabels(timeMs);
                    RenderPlacementOverlay();
                });
                idx++;
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            Log.Error("UI", "Playback stream failed", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                VideoInfoLabel.Text = $"Playback error: {ex.Message}";
                StopPlaybackStream();
                PlayPauseButton.Content = "▶ Play";
            });
        }
    }

    private void SetSliderInternal(int valueMs)
    {
        _isSliderInternalUpdate = true;
        try { TimeSlider.Value = Math.Clamp(valueMs, 0, (int)TimeSlider.Maximum); }
        finally { _isSliderInternalUpdate = false; }
    }

    private bool IsPlaybackActive() => _playbackStreamTask is not null;

    private void OnPlayTick(object? sender, EventArgs e) { /* deprecated; streaming path drives playback */ }

    private void SeekRelative(int deltaMs)
    {
        var v = Math.Clamp(_currentTimeMs + deltaMs, 0, (int)TimeSlider.Maximum);
        TimeSlider.Value = v;
    }

    private static string FormatTime(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    private static bool IsHorizontalSlide(AnimationStyle style) =>
        style is AnimationStyle.SlideLeft or AnimationStyle.SlideRight;

    private static Animation CloneAnimation(Animation source) => new()
    {
        Enter = source.Enter,
        Exit = source.Exit,
        EnterMs = source.EnterMs,
        ExitMs = source.ExitMs
    };

    private (double centerX, double centerY, Animation? animationOverride) ResolveClickPlacement(
        Template template, double clickX, double clickY)
    {
        (clickX, clickY) = ApplySnapToGrid(clickX, clickY);
        var anim = template.Animation;
        bool horizontalTemplate = IsHorizontalSlide(anim.Enter) || IsHorizontalSlide(anim.Exit);
        if (!horizontalTemplate || _project.VideoResolution.Width <= 0 || _project.VideoResolution.Height <= 0)
            return (clickX, clickY, null);

        bool fromLeft = clickX < 0.5;
        double halfWNorm = Math.Min(0.5, (template.Width / 2.0) / _project.VideoResolution.Width);
        double halfHNorm = Math.Min(0.5, (template.Height / 2.0) / _project.VideoResolution.Height);
        double centerX = fromLeft ? halfWNorm : 1.0 - halfWNorm;
        double centerY = Math.Clamp(clickY, halfHNorm, 1.0 - halfHNorm);
        var sideStyle = fromLeft ? AnimationStyle.SlideLeft : AnimationStyle.SlideRight;

        var overrideAnim = CloneAnimation(anim);
        if (IsHorizontalSlide(overrideAnim.Enter)) overrideAnim.Enter = sideStyle;
        if (IsHorizontalSlide(overrideAnim.Exit)) overrideAnim.Exit = sideStyle;
        return (centerX, centerY, overrideAnim);
    }

    private async Task CheckDependenciesAsync(bool promptIfMissing)
    {
        try
        {
            var statuses = await _api.Dependencies.CheckAsync();
            var missing = statuses.Where(s => s.State != DependencyState.Installed).Select(s => s.Name).ToList();
            _dependenciesMissing = missing.Count > 0;
            InstallDepsButton.IsVisible = missing.Count > 0;
            DashboardDependencyPanel.IsVisible = _dependenciesMissing;
            if (_dependenciesMissing)
            {
                DashboardDependencyMessage.Text = $"Missing dependency: {string.Join(", ", missing)}. Install it to continue.";
                DashboardInstallStatus.Text = "";
            }
            else
            {
                DashboardInstallProgress.IsVisible = false;
                DashboardInstallStatus.Text = "";
            }

            DashboardNewProjectButton.IsEnabled = !_dependenciesMissing;
            DashboardOpenProjectButton.IsEnabled = !_dependenciesMissing;
            DashboardOpenLogButton.IsEnabled = !_dependenciesMissing;
            RecentProjectsList.IsEnabled = !_dependenciesMissing;
            DashboardInstallDepsButton.IsEnabled = _dependenciesMissing;
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Dependency check failed", ex);
            VideoInfoLabel.Text = $"Dependency check failed: {ex.Message}";
            DashboardDependencyPanel.IsVisible = true;
            DashboardDependencyMessage.Text = $"Dependency check failed: {ex.Message}";
            DashboardInstallStatus.Text = "";
            DashboardInstallProgress.IsVisible = false;
        }
    }

    private async void OnInstallDeps(object? sender, RoutedEventArgs e) => await InstallDepsAsync();

    private async Task InstallDepsAsync()
    {
        InstallDepsButton.IsEnabled = false;
        DashboardInstallDepsButton.IsEnabled = false;
        DashboardInstallProgress.IsVisible = true;
        DashboardInstallStatus.Text = "Installing...";
        var progress = new Progress<DependencyInstallProgress>(p =>
        {
            var text = $"Install {p.Name}: {p.Stage} {p.Detail}".Trim();
            ExportStatus.Text = text;
            DashboardInstallStatus.Text = text;
        });
        try
        {
            await _api.Dependencies.InstallMissingAsync(progress);
            ExportStatus.Text = "Install complete.";
            DashboardInstallStatus.Text = "Install complete.";
        }
        catch (OperationCanceledException)
        {
            ExportStatus.Text = "Install interrupted. You can retry.";
            DashboardInstallStatus.Text = "Install interrupted. You can retry.";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Install failed", ex);
            ExportStatus.Text = $"Install failed: {ex.Message}";
            DashboardInstallStatus.Text = $"Install failed: {ex.Message}";
        }
        finally
        {
            await CheckDependenciesAsync(promptIfMissing: false);
            InstallDepsButton.IsEnabled = true;
            DashboardInstallProgress.IsVisible = false;
            DashboardInstallDepsButton.IsEnabled = _dependenciesMissing;
        }
    }

    private static void OpenInShell(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path) ?? path;
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", dir);
            else
                Process.Start("xdg-open", dir);
        }
        catch (Exception ex) { Log.Error("UI", "Open folder failed", ex); }
    }

    private void RefreshTemplates()
    {
        Templates.Clear();
        foreach (var t in _api.ListTemplates(_project)) Templates.Add(t);
    }

    private void RefreshInstances(string? preserveSelectedId = null)
    {
        preserveSelectedId ??= (InstancesList.SelectedItem as InstanceListItem)?.Instance.Id;
        Instances.Clear();
        foreach (var i in _api.ListInstances(_project).OrderBy(i => i.StartMs))
        {
            var templateName = _project.Templates.FirstOrDefault(t => t.Id == i.TemplateId)?.Name ?? i.TemplateId;
            var startTs = TimeSpan.FromMilliseconds(i.StartMs);
            Instances.Add(new InstanceListItem
            {
                Instance = i,
                TemplateName = templateName,
                TimeLabel = $"{(int)startTs.TotalMinutes}:{startTs.Seconds:00}.{startTs.Milliseconds:000}",
                TextPreview = BuildTextPreview(i)
            });
        }

        if (!string.IsNullOrWhiteSpace(preserveSelectedId))
            InstancesList.SelectedItem = Instances.FirstOrDefault(x => x.Instance.Id == preserveSelectedId);
    }

    private static string BuildTextPreview(TemplateInstance i)
    {
        if (i.TextValues is null || i.TextValues.Count == 0) return string.Empty;
        var joined = string.Join(" / ",
            i.TextValues.Values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Replace('\r', ' ').Replace('\n', ' ').Trim()));
        if (joined.Length > 80) joined = joined.Substring(0, 79) + "…";
        return joined;
    }

    private TemplateInstance? SelectedInstance =>
        InstancesList.SelectedItem is InstanceListItem item ? item.Instance : null;

    private async void OnOpenVideo(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var isReplacement = !string.IsNullOrWhiteSpace(_project.VideoPath);
            var oldPath = _project.VideoPath;
            var oldDurationMs = _project.VideoDurationMs;
            var instanceCount = _project.Instances.Count;

            int shiftMs = 0;
            if (isReplacement && instanceCount > 0)
            {
                var newInfo = await _api.ProbeVideoAsync(path);
                if (newInfo.DurationMs != oldDurationMs)
                {
                    var chosen = await ReplaceVideoShiftDialog.ShowAsync(
                        this,
                        Path.GetFileName(oldPath) ?? "(unknown)",
                        oldDurationMs,
                        Path.GetFileName(path) ?? "(new)",
                        newInfo.DurationMs,
                        instanceCount);
                    if (chosen is null) return; // user cancelled
                    shiftMs = chosen.Value;
                }
            }

            _project = shiftMs == 0
                ? await _api.SetVideoAsync(_project, path)
                : await _api.ReplaceVideoAsync(_project, path, shiftMs);

            ClearPlaybackFrameCache();
            TimeSlider.Maximum = Math.Max(1, _project.VideoDurationMs);
            TimeSlider.Value = 0;
            VideoInfoLabel.Text = $"{Path.GetFileName(path)} • {_project.VideoResolution.Width}x{_project.VideoResolution.Height} @ {_project.VideoFps:0.##} fps • {_project.VideoDurationMs / 1000.0:0.0}s";
            await RefreshPreviewAsync();
            await AutoSaveAsync(shiftMs == 0 ? "set-video" : "replace-video");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "OpenVideo failed", ex);
            VideoInfoLabel.Text = $"Error: {ex.Message}";
        }
    }

    private void CommitInstanceEdit()
    {
        _ = ApplyInstanceFromEditorAsync("update-instance");
    }

    private async Task RefreshPreviewAsync()
    {
        if (string.IsNullOrEmpty(_project.VideoPath)) return;
        Volatile.Write(ref _previewRenderRequestedTimeMs, GetPreviewRequestTimeMs(_currentTimeMs));
        if (Interlocked.Exchange(ref _previewRenderBusy, 1) == 1)
        {
            Volatile.Write(ref _previewRenderPending, 1);
            return;
        }

        try
        {
            while (true)
            {
                Volatile.Write(ref _previewRenderPending, 0);
                var requestTimeMs = Volatile.Read(ref _previewRenderRequestedTimeMs);
                byte[] bytes;
                if (!TryGetPlaybackCachedFrame(requestTimeMs, out var cached))
                {
                    bytes = await _api.RenderFrameAsync(_project, requestTimeMs);
                    if (IsPlaybackActive())
                        AddPlaybackCachedFrame(requestTimeMs, bytes);
                }
                else
                {
                    bytes = cached;
                }
                using var ms = new MemoryStream(bytes);
                PreviewImage.Source = new Bitmap(ms);
                RenderPlacementOverlay();
                if (Volatile.Read(ref _previewRenderPending) == 0)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("UI", "RefreshPreview failed", ex);
            VideoInfoLabel.Text = $"Preview error: {ex.Message}";
        }
        finally
        {
            Volatile.Write(ref _previewRenderBusy, 0);
            if (Volatile.Read(ref _previewRenderPending) == 1)
                _ = RefreshPreviewAsync();
        }
    }

    private int GetPreviewRequestTimeMs(int timeMs)
    {
        if (!IsPlaybackActive()) return timeMs;
        return Math.Max(0, (timeMs / PlaybackPreviewQuantizeMs) * PlaybackPreviewQuantizeMs);
    }

    private bool TryGetPlaybackCachedFrame(int timeMs, out byte[] bytes) =>
        _playbackFrameCache.TryGetValue(timeMs, out bytes!);

    private void AddPlaybackCachedFrame(int timeMs, byte[] bytes)
    {
        if (_playbackFrameCache.ContainsKey(timeMs)) return;
        _playbackFrameCache[timeMs] = bytes;
        _playbackFrameCacheOrder.Enqueue(timeMs);
        while (_playbackFrameCacheOrder.Count > PlaybackFrameCacheMax)
        {
            var old = _playbackFrameCacheOrder.Dequeue();
            _playbackFrameCache.Remove(old);
        }
    }

    private void ClearPlaybackFrameCache()
    {
        _playbackFrameCache.Clear();
        _playbackFrameCacheOrder.Clear();
    }

    private void UpdatePlaybackTimeLabels(int timeMs)
    {
        TimeLabel.Text = $"{timeMs} ms";
        PlaybackTimeLabel.Text = $"{FormatTime(timeMs)} / {FormatTime(_project.VideoDurationMs)}";
    }

    private (double x, double y) GetNormalizedPreviewPoint(Avalonia.Point pos)
    {
        double width = Math.Max(1, PreviewImage.Bounds.Width);
        double height = Math.Max(1, PreviewImage.Bounds.Height);
        double cx = Math.Clamp(pos.X / width, 0, 1);
        double cy = Math.Clamp(pos.Y / height, 0, 1);
        return (cx, cy);
    }

    private (double x, double y) ApplySnapToGrid(double x, double y)
    {
        if (!_settings.SnapToGridEnabled) return (x, y);
        int divisions = Math.Max(2, _settings.SnapGridDivisions);
        double step = 1.0 / divisions;
        double snappedX = Math.Clamp(Math.Round(x / step) * step, 0, 1);
        double snappedY = Math.Clamp(Math.Round(y / step) * step, 0, 1);
        return (snappedX, snappedY);
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_armedTemplateId is null || PreviewImage.Source is null)
        {
            _previewHoverNormalized = null;
            RenderPlacementOverlay();
            return;
        }

        _previewHoverNormalized = GetNormalizedPreviewPoint(e.GetPosition(PreviewImage));
        RenderPlacementOverlay();
    }

    private void OnPreviewPointerExited(object? sender, PointerEventArgs e)
    {
        _previewHoverNormalized = null;
        RenderPlacementOverlay();
    }

    private void RenderPlacementOverlay()
    {
        PlacementOverlay.Children.Clear();

        if (PreviewImage.Source is null) return;
        double width = PreviewImage.Bounds.Width;
        double height = PreviewImage.Bounds.Height;
        if (width <= 1 || height <= 1) return;

        PlacementOverlay.Width = width;
        PlacementOverlay.Height = height;

        if (_settings.SnapToGridEnabled)
        {
            int divisions = Math.Max(2, _settings.SnapGridDivisions);
            var gridBrush = new AvaloniaMedia.SolidColorBrush(AvaloniaMedia.Color.FromArgb(70, 255, 255, 255));
            for (int i = 1; i < divisions; i++)
            {
                double x = i * width / divisions;
                double y = i * height / divisions;
                PlacementOverlay.Children.Add(new LineShape
                {
                    StartPoint = new Avalonia.Point(x, 0),
                    EndPoint = new Avalonia.Point(x, height),
                    Stroke = gridBrush,
                    StrokeThickness = 1
                });
                PlacementOverlay.Children.Add(new LineShape
                {
                    StartPoint = new Avalonia.Point(0, y),
                    EndPoint = new Avalonia.Point(width, y),
                    Stroke = gridBrush,
                    StrokeThickness = 1
                });
            }
        }

        if (_armedTemplateId is null || _previewHoverNormalized is not { } hover) return;
        var template = _project.Templates.FirstOrDefault(t => t.Id == _armedTemplateId);
        if (template is null) return;

        var placement = ResolveClickPlacement(template, hover.x, hover.y);
        double centerX = placement.centerX * width;
        double centerY = placement.centerY * height;

        double previewWidth = _project.VideoResolution.Width > 0
            ? (template.Width / (double)_project.VideoResolution.Width) * width
            : 0;
        double previewHeight = _project.VideoResolution.Height > 0
            ? (template.Height / (double)_project.VideoResolution.Height) * height
            : 0;

        previewWidth = Math.Clamp(previewWidth, 4, width);
        previewHeight = Math.Clamp(previewHeight, 4, height);

        var rect = new RectangleShape
        {
            Width = previewWidth,
            Height = previewHeight,
            StrokeThickness = 2,
            Stroke = new AvaloniaMedia.SolidColorBrush(AvaloniaMedia.Color.FromArgb(220, 72, 197, 255)),
            Fill = new AvaloniaMedia.SolidColorBrush(AvaloniaMedia.Color.FromArgb(40, 72, 197, 255)),
            RadiusX = 4,
            RadiusY = 4
        };
        Canvas.SetLeft(rect, centerX - previewWidth / 2);
        Canvas.SetTop(rect, centerY - previewHeight / 2);
        PlacementOverlay.Children.Add(rect);

        var centerDot = new EllipseShape
        {
            Width = 8,
            Height = 8,
            Fill = new AvaloniaMedia.SolidColorBrush(AvaloniaMedia.Color.FromArgb(255, 72, 197, 255))
        };
        Canvas.SetLeft(centerDot, centerX - 4);
        Canvas.SetTop(centerDot, centerY - 4);
        PlacementOverlay.Children.Add(centerDot);
    }

    private async void OnPreviewClicked(object? sender, PointerPressedEventArgs e)
    {
        if (_armedTemplateId is null || PreviewImage.Source is null) return;
        var keyModifiers = e.KeyModifiers;
        var (cx, cy) = GetNormalizedPreviewPoint(e.GetPosition(PreviewImage));

        if (IsPlaybackActive()) TogglePlay();

        var template = _api.GetTemplate(_project, _armedTemplateId);
        if (keyModifiers.HasFlag(KeyModifiers.Shift))
        {
            await RetimeLatestInstanceOfArmedTemplateAsync(template);
            return;
        }
        var placement = ResolveClickPlacement(template, cx, cy);
        var values = template.Elements.OfType<TextElement>().ToDictionary(t => t.Id, t => t.DefaultText ?? "");
        var inst = _api.AddInstance(_project, new AddInstanceRequest(template.Id, placement.centerX, placement.centerY, _currentTimeMs, null, values, placement.animationOverride));
        RefreshInstances(inst.Id);
        InstancesList.SelectedItem = Instances.FirstOrDefault(item => item.Instance.Id == inst.Id);
        if (_compactMode)
        {
            ShowCompactOverlayForCurrentInstance(template);
        }
        else
        {
            FocusFirstInstanceTextFieldForReplace();
        }
        await RefreshPreviewAsync();
        await AutoSaveAsync("add-instance");
    }

    private async Task RetimeLatestInstanceOfArmedTemplateAsync(Template template)
    {
        var latest = _project.Instances
            .Where(i => i.TemplateId == template.Id)
            .OrderByDescending(i => i.StartMs)
            .ThenByDescending(i => i.Id)
            .FirstOrDefault();
        if (latest is null)
        {
            ExportStatus.Text = $"No existing '{template.Name}' instance to retime.";
            return;
        }

        _api.UpdateInstance(_project, new UpdateInstanceRequest(
            latest.Id,
            StartMs: _currentTimeMs));
        RefreshInstances(latest.Id);
        InstancesList.SelectedItem = Instances.FirstOrDefault(item => item.Instance.Id == latest.Id);
        await RefreshPreviewAsync();
        await AutoSaveAsync("retime-instance");
        ExportStatus.Text = $"Retimed latest '{template.Name}' to {_currentTimeMs} ms.";
    }

    private async void OnDeleteInstance(object? sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } i) return;
        _api.DeleteInstance(_project, i.Id);
        RefreshInstances();
        await AutoSaveAsync("delete-instance");
    }

    private void UpdateInstanceEditor()
    {
        if (SelectedInstance is not { } i)
        {
            InstanceEditor.IsVisible = false;
            _instanceEditorInstanceId = null;
            InstanceTextFields.Clear();
            return;
        }

        InstanceEditor.IsVisible = true;
        _instanceEditorInstanceId = i.Id;
        _isApplyingInstanceEditor = true;
        try
        {
            InstanceStartBox.Text = i.StartMs.ToString();
            InstanceDurationBox.Text = i.DurationMs.ToString();
            InstanceXBox.Text = i.Center.X.ToString("0.###");
            InstanceYBox.Text = i.Center.Y.ToString("0.###");
            var template = _project.Templates.FirstOrDefault(t => t.Id == i.TemplateId);
            InstanceTemplateNameLabel.Text = template is not null ? $"Template: {template.Name}" : $"Template ID: {i.TemplateId}";

            InstanceTextFields.Clear();
            if (template is not null)
            {
                int row = 1;
                foreach (var te in template.Elements.OfType<TextElement>())
                {
                    InstanceTextFields.Add(new InstanceTextFieldItem
                    {
                        ElementId = te.Id,
                        Label = $"Row {row++}",
                        Value = i.TextValues.TryGetValue(te.Id, out var v) ? v : te.DefaultText
                    });
                }
            }
            else
            {
                foreach (var kv in i.TextValues)
                {
                    InstanceTextFields.Add(new InstanceTextFieldItem
                    {
                        ElementId = kv.Key,
                        Label = kv.Key,
                        Value = kv.Value
                    });
                }
            }
        }
        finally
        {
            _isApplyingInstanceEditor = false;
        }
    }

    private async void OnApplyInstance(object? sender, RoutedEventArgs e)
    {
        await ApplyInstanceFromEditorAsync("update-instance");
    }

    private async Task ApplyInstanceFromEditorAsync(string reason)
    {
        if (_isApplyingProject || _isApplyingInstanceEditor) return;
        if (string.IsNullOrWhiteSpace(_instanceEditorInstanceId)) return;
        var i = _project.Instances.FirstOrDefault(x => x.Id == _instanceEditorInstanceId);
        if (i is null) return;

        var prevStart = i.StartMs;
        var req = new UpdateInstanceRequest(
            i.Id,
            double.TryParse(InstanceXBox.Text, out var x) ? x : null,
            double.TryParse(InstanceYBox.Text, out var y) ? y : null,
            int.TryParse(InstanceStartBox.Text, out var s) ? s : null,
            int.TryParse(InstanceDurationBox.Text, out var d) ? d : null,
            InstanceTextFields.ToDictionary(t => t.ElementId, t => t.Value ?? string.Empty),
            null);
        _api.UpdateInstance(_project, req);

        // Only rebuild the instance ListBox when something visible there changed
        // (StartMs affects ordering + time label). Otherwise skip the refresh so the
        // currently focused text field keeps focus during typing.
        if (i.StartMs != prevStart)
        {
            RefreshInstances(i.Id);
        }
        else
        {
            // Live-update the preview row in place so the text snippet stays current.
            var item = Instances.FirstOrDefault(it => it.Instance.Id == i.Id);
            if (item is not null) item.TextPreview = BuildTextPreview(i);
        }

        await AutoSaveAsync(reason);
        await RefreshPreviewAsync();
    }

    private async void OnInstanceTextFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        await ApplyInstanceFromEditorAsync("update-instance-text");
    }

    private void OnInstanceTextFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox current || e.Key != Key.Tab) return;
        if (current.Tag is not string currentId) return;

        var ids = InstanceTextFields.Select(f => f.ElementId).ToList();
        var index = ids.IndexOf(currentId);
        if (index < 0) return;

        var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
        var nextIndex = index + delta;
        if (nextIndex < 0 || nextIndex >= ids.Count) return;

        e.Handled = true;
        var nextId = ids[nextIndex];
        var nextBox = FindInstanceTextBoxById(nextId);
        if (nextBox is null) return;
        nextBox.Focus();
        nextBox.SelectAll();
    }

    private TextBox? FindInstanceTextBoxById(string elementId) =>
        InstanceTextFieldsList
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.Tag is string id && id == elementId);

    private void FocusFirstInstanceTextFieldForReplace()
    {
        if (InstanceTextFields.Count == 0) return;
        var firstId = InstanceTextFields[0].ElementId;
        // Retry across UI passes — the ListBox may not yet have realized its item
        // containers right after the instance was added.
        TryFocusInstanceTextBox(firstId, attemptsRemaining: 8);
    }

    private void TryFocusInstanceTextBox(string elementId, int attemptsRemaining)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = FindInstanceTextBoxById(elementId);
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
                return;
            }
            if (attemptsRemaining > 0)
                TryFocusInstanceTextBox(elementId, attemptsRemaining - 1);
        }, DispatcherPriority.Background);
    }

    private void UpdateTemplateEditor()
    {
        if (TemplatesList.SelectedItem is not Template t)
        {
            TemplateEditorScroll.IsVisible = false;
            TemplateJsonBox.Text = "";
            ElementsListItems.Clear();
            ElementEditorPanel.IsVisible = false;
            return;
        }

        TemplateEditorScroll.IsVisible = true;
        _isApplyingTemplateEditor = true;
        try
        {
            TemplateNameBox.Text = t.Name;
            TemplateWidthBox.Text = t.Width.ToString();
            TemplateHeightBox.Text = t.Height.ToString();
            TemplateDurationBox.Text = t.DefaultDurationMs.ToString();
            TemplateEnterBox.SelectedItem = t.Animation.Enter;
            TemplateExitBox.SelectedItem = t.Animation.Exit;
            TemplateEnterMsBox.Text = t.Animation.EnterMs.ToString();
            TemplateExitMsBox.Text = t.Animation.ExitMs.ToString();
            TemplateSoundEnterBox.Text = t.Sound.EnterFile ?? "";
            TemplateSoundExitBox.Text = t.Sound.ExitFile ?? "";
            TemplateSoundVolumeSlider.Value = t.Sound.Volume;
            TemplateSoundVolumeLabel.Text = t.Sound.Volume.ToString("0.00");
            TemplateJsonBox.Text = JsonSerializer.Serialize(t, ProjectJson.Options);

            RebuildElementsList(t, preserveSelectionId: (ElementsList.SelectedItem as ElementListItem)?.Element.Id);
        }
        finally
        {
            _isApplyingTemplateEditor = false;
        }

        UpdateElementEditor();
    }

    private void RebuildElementsList(Template t, string? preserveSelectionId)
    {
        ElementsListItems.Clear();
        foreach (var el in t.Elements)
            ElementsListItems.Add(BuildElementListItem(el));

        if (preserveSelectionId is not null)
        {
            var match = ElementsListItems.FirstOrDefault(x => x.Element.Id == preserveSelectionId);
            if (match is not null) ElementsList.SelectedItem = match;
        }
    }

    private static ElementListItem BuildElementListItem(Element el) => el switch
    {
        ShapeElement s => new ElementListItem
        {
            Element = s,
            Icon = s.Shape switch { ShapeKind.Ellipse => "⬭", ShapeKind.RoundedRectangle => "▢", _ => "▭" },
            Summary = $"{s.Shape} {s.Width}×{s.Height}"
        },
        TextElement tx => new ElementListItem
        {
            Element = tx,
            Icon = "T",
            Summary = $"Text: \"{Truncate(tx.DefaultText, 28)}\" ({tx.FontSize}pt)"
        },
        _ => new ElementListItem { Element = el, Icon = "?", Summary = el.Id }
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private void UpdateElementEditor()
    {
        var item = ElementsList.SelectedItem as ElementListItem;
        if (item is null)
        {
            ElementEditorPanel.IsVisible = false;
            return;
        }

        ElementEditorPanel.IsVisible = true;
        _isApplyingTemplateEditor = true;
        try
        {
            var el = item.Element;
            ElementOffsetXBox.Text = el.OffsetX.ToString();
            ElementOffsetYBox.Text = el.OffsetY.ToString();
            ElementWidthBox.Text = el.Width.ToString();
            ElementHeightBox.Text = el.Height.ToString();

            if (el is ShapeElement s)
            {
                ElementEditorHeader.Text = $"Shape — {s.Shape}";
                ShapeElementEditor.IsVisible = true;
                TextElementEditor.IsVisible = false;
                ShapeKindBox.SelectedItem = s.Shape;
                ShapeFillBox.Text = s.Fill.ToHex();
                ShapeBorderColorBox.Text = s.BorderColor.ToHex();
                ShapeBorderThicknessBox.Text = s.BorderThickness.ToString();
                ShapeCornerRadiusBox.Text = s.CornerRadius.ToString();
                UpdateColorSwatch(ShapeFillSwatch, s.Fill);
                UpdateColorSwatch(ShapeBorderSwatch, s.BorderColor);
            }
            else if (el is TextElement tx)
            {
                ElementEditorHeader.Text = "Text element";
                ShapeElementEditor.IsVisible = false;
                TextElementEditor.IsVisible = true;
                TextFontFamilyBox.Text = tx.FontFamily;
                TextFontSizeBox.Text = tx.FontSize.ToString();
                TextBoldBox.IsChecked = tx.Bold;
                TextItalicBox.IsChecked = tx.Italic;
                TextColorBox.Text = tx.TextColor.ToHex();
                TextHAlignBox.SelectedItem = tx.HAlign;
                TextVAlignBox.SelectedItem = tx.VAlign;
                TextLineSpacingBox.Text = tx.LineSpacing.ToString();
                TextDefaultTextBox.Text = tx.DefaultText;
                UpdateColorSwatch(TextColorSwatch, tx.TextColor);
            }
        }
        finally
        {
            _isApplyingTemplateEditor = false;
        }
    }

    private static void UpdateColorSwatch(Border swatch, Color c)
    {
        swatch.Background = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    // ── Auto-apply wiring ──────────────────────────────────────────────────

    private void WireTemplateEditorAutoApply()
    {
        // Template-level
        TemplateNameBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateWidthBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateHeightBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateDurationBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateEnterMsBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateExitMsBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateEnterBox.SelectionChanged += (_, _) => CommitTemplateEditor();
        TemplateExitBox.SelectionChanged += (_, _) => CommitTemplateEditor();
        TemplateSoundEnterBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateSoundExitBox.LostFocus += (_, _) => CommitTemplateEditor();
        TemplateSoundVolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                TemplateSoundVolumeLabel.Text = TemplateSoundVolumeSlider.Value.ToString("0.00");
                CommitTemplateEditor();
            }
        };
        TemplateSoundEnterBrowse.Click += async (_, _) => await BrowseForSoundAsync(TemplateSoundEnterBox);
        TemplateSoundExitBrowse.Click += async (_, _) => await BrowseForSoundAsync(TemplateSoundExitBox);
        TemplateSoundEnterClear.Click += (_, _) => { TemplateSoundEnterBox.Text = ""; CommitTemplateEditor(); };
        TemplateSoundExitClear.Click += (_, _) => { TemplateSoundExitBox.Text = ""; CommitTemplateEditor(); };

        // Elements list + toolbar
        ElementsList.SelectionChanged += (_, _) => UpdateElementEditor();
        AddShapeElementButton.Click += async (_, _) => await OnAddShapeElementAsync();
        AddTextElementButton.Click += async (_, _) => await OnAddTextElementAsync();
        DuplicateElementButton.Click += async (_, _) => await OnDuplicateElementAsync();
        DeleteElementButton.Click += async (_, _) => await OnDeleteElementAsync();
        MoveElementUpButton.Click += async (_, _) => await OnMoveElementAsync(-1);
        MoveElementDownButton.Click += async (_, _) => await OnMoveElementAsync(+1);

        // Common element fields
        ElementOffsetXBox.LostFocus += (_, _) => CommitElementEditor();
        ElementOffsetYBox.LostFocus += (_, _) => CommitElementEditor();
        ElementWidthBox.LostFocus += (_, _) => CommitElementEditor();
        ElementHeightBox.LostFocus += (_, _) => CommitElementEditor();

        // Shape fields
        ShapeKindBox.SelectionChanged += (_, _) => CommitElementEditor();
        ShapeFillBox.LostFocus += (_, _) => CommitElementEditor();
        ShapeBorderColorBox.LostFocus += (_, _) => CommitElementEditor();
        ShapeBorderThicknessBox.LostFocus += (_, _) => CommitElementEditor();
        ShapeCornerRadiusBox.LostFocus += (_, _) => CommitElementEditor();

        // Text fields
        TextFontFamilyBox.LostFocus += (_, _) => CommitElementEditor();
        TextFontSizeBox.LostFocus += (_, _) => CommitElementEditor();
        TextBoldBox.IsCheckedChanged += (_, _) => CommitElementEditor();
        TextItalicBox.IsCheckedChanged += (_, _) => CommitElementEditor();
        TextColorBox.LostFocus += (_, _) => CommitElementEditor();
        TextHAlignBox.SelectionChanged += (_, _) => CommitElementEditor();
        TextVAlignBox.SelectionChanged += (_, _) => CommitElementEditor();
        TextLineSpacingBox.LostFocus += (_, _) => CommitElementEditor();
        TextDefaultTextBox.LostFocus += (_, _) => CommitElementEditor();
    }

    private async Task BrowseForSoundAsync(TextBox target)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose sound file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.m4a", "*.aac" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        target.Text = path;
        CommitTemplateEditor();
    }

    private async void CommitTemplateEditor()
    {
        if (_isApplyingTemplateEditor) return;
        if (TemplatesList.SelectedItem is not Template t) return;

        try
        {
            t.Name = string.IsNullOrWhiteSpace(TemplateNameBox.Text) ? t.Name : TemplateNameBox.Text.Trim();
            if (int.TryParse(TemplateWidthBox.Text, out var w)) t.Width = Math.Max(10, w);
            if (int.TryParse(TemplateHeightBox.Text, out var h)) t.Height = Math.Max(10, h);
            if (int.TryParse(TemplateDurationBox.Text, out var dur)) t.DefaultDurationMs = Math.Max(1, dur);
            if (TemplateEnterBox.SelectedItem is AnimationStyle enter) t.Animation.Enter = enter;
            if (TemplateExitBox.SelectedItem is AnimationStyle exit) t.Animation.Exit = exit;
            if (int.TryParse(TemplateEnterMsBox.Text, out var enterMs)) t.Animation.EnterMs = Math.Max(0, enterMs);
            if (int.TryParse(TemplateExitMsBox.Text, out var exitMs)) t.Animation.ExitMs = Math.Max(0, exitMs);
            t.Sound.EnterFile = string.IsNullOrWhiteSpace(TemplateSoundEnterBox.Text) ? null : TemplateSoundEnterBox.Text.Trim();
            t.Sound.ExitFile = string.IsNullOrWhiteSpace(TemplateSoundExitBox.Text) ? null : TemplateSoundExitBox.Text.Trim();
            t.Sound.Volume = Math.Clamp(TemplateSoundVolumeSlider.Value, 0, 4);

            _api.UpdateTemplate(_project, t);
            _isApplyingTemplateEditor = true;
            try
            {
                TemplateJsonBox.Text = JsonSerializer.Serialize(t, ProjectJson.Options);
                RefreshTemplates();
                TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == t.Id);
            }
            finally { _isApplyingTemplateEditor = false; }
            await AutoSaveAsync("update-template");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "CommitTemplateEditor failed", ex);
            ExportStatus.Text = $"Template error: {ex.Message}";
        }
    }

    private async void CommitElementEditor()
    {
        if (_isApplyingTemplateEditor) return;
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;

        var el = item.Element;
        try
        {
            if (int.TryParse(ElementOffsetXBox.Text, out var ox)) el.OffsetX = ox;
            if (int.TryParse(ElementOffsetYBox.Text, out var oy)) el.OffsetY = oy;
            if (int.TryParse(ElementWidthBox.Text, out var ew)) el.Width = Math.Max(1, ew);
            if (int.TryParse(ElementHeightBox.Text, out var eh)) el.Height = Math.Max(1, eh);

            if (el is ShapeElement s)
            {
                if (ShapeKindBox.SelectedItem is ShapeKind sk) s.Shape = sk;
                if (TryParseColor(ShapeFillBox.Text, out var fill)) { s.Fill = fill; UpdateColorSwatch(ShapeFillSwatch, fill); }
                if (TryParseColor(ShapeBorderColorBox.Text, out var bc)) { s.BorderColor = bc; UpdateColorSwatch(ShapeBorderSwatch, bc); }
                if (int.TryParse(ShapeBorderThicknessBox.Text, out var bt)) s.BorderThickness = Math.Max(0, bt);
                if (int.TryParse(ShapeCornerRadiusBox.Text, out var cr)) s.CornerRadius = Math.Max(0, cr);
            }
            else if (el is TextElement tx)
            {
                if (!string.IsNullOrWhiteSpace(TextFontFamilyBox.Text)) tx.FontFamily = TextFontFamilyBox.Text.Trim();
                if (int.TryParse(TextFontSizeBox.Text, out var fs)) tx.FontSize = Math.Max(4, fs);
                tx.Bold = TextBoldBox.IsChecked == true;
                tx.Italic = TextItalicBox.IsChecked == true;
                if (TryParseColor(TextColorBox.Text, out var tc)) { tx.TextColor = tc; UpdateColorSwatch(TextColorSwatch, tc); }
                if (TextHAlignBox.SelectedItem is HorizontalAlign ha) tx.HAlign = ha;
                if (TextVAlignBox.SelectedItem is VerticalAlign va) tx.VAlign = va;
                if (int.TryParse(TextLineSpacingBox.Text, out var ls)) tx.LineSpacing = Math.Max(0, ls);
                tx.DefaultText = TextDefaultTextBox.Text ?? "";
            }

            _api.UpdateTemplate(_project, t);
            _isApplyingTemplateEditor = true;
            try
            {
                TemplateJsonBox.Text = JsonSerializer.Serialize(t, ProjectJson.Options);
                // Update list-item summary in-place
                var rebuilt = BuildElementListItem(el);
                var idx = ElementsListItems.IndexOf(item);
                if (idx >= 0)
                {
                    ElementsListItems[idx] = rebuilt;
                    ElementsList.SelectedItem = rebuilt;
                }
            }
            finally { _isApplyingTemplateEditor = false; }
            await AutoSaveAsync("update-element");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "CommitElementEditor failed", ex);
            ExportStatus.Text = $"Element error: {ex.Message}";
        }
    }

    private static bool TryParseColor(string? hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { c = Color.FromHex(hex.Trim()); return true; }
        catch { return false; }
    }

    private async Task OnAddShapeElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        var el = new ShapeElement
        {
            Id = Guid.NewGuid().ToString("n"),
            OffsetX = 0, OffsetY = 0,
            Width = Math.Max(40, t.Width / 2),
            Height = Math.Max(40, t.Height / 2),
            Shape = ShapeKind.Rectangle,
            Fill = Color.White,
            BorderColor = Color.Black,
            BorderThickness = 2,
            CornerRadius = 0
        };
        t.Elements.Add(el);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == el.Id);
        await AutoSaveAsync("add-shape-element");
        await RefreshPreviewAsync();
    }

    private async Task OnAddTextElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        var el = new TextElement
        {
            Id = Guid.NewGuid().ToString("n"),
            OffsetX = 8, OffsetY = 8,
            Width = Math.Max(40, t.Width - 16),
            Height = Math.Max(20, t.Height - 16),
            FontFamily = "Segoe UI",
            FontSize = 24,
            TextColor = Color.Black,
            HAlign = HorizontalAlign.Center,
            VAlign = VerticalAlign.Center,
            DefaultText = "Text"
        };
        t.Elements.Add(el);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == el.Id);
        await AutoSaveAsync("add-text-element");
        await RefreshPreviewAsync();
    }

    private async Task OnDuplicateElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;
        var json = JsonSerializer.Serialize<Element>(item.Element, ProjectJson.Options);
        var dup = JsonSerializer.Deserialize<Element>(json, ProjectJson.Options);
        if (dup is null) return;
        dup.Id = Guid.NewGuid().ToString("n");
        dup.OffsetX += 10;
        dup.OffsetY += 10;
        var idx = t.Elements.IndexOf(item.Element);
        t.Elements.Insert(Math.Max(0, idx + 1), dup);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == dup.Id);
        await AutoSaveAsync("duplicate-element");
        await RefreshPreviewAsync();
    }

    private async Task OnDeleteElementAsync()
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;
        t.Elements.Remove(item.Element);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        await AutoSaveAsync("delete-element");
        await RefreshPreviewAsync();
    }

    private async Task OnMoveElementAsync(int delta)
    {
        if (TemplatesList.SelectedItem is not Template t) return;
        if (ElementsList.SelectedItem is not ElementListItem item) return;
        var idx = t.Elements.IndexOf(item.Element);
        var newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= t.Elements.Count) return;
        (t.Elements[idx], t.Elements[newIdx]) = (t.Elements[newIdx], t.Elements[idx]);
        _api.UpdateTemplate(_project, t);
        UpdateTemplateEditor();
        ElementsList.SelectedItem = ElementsListItems.FirstOrDefault(x => x.Element.Id == item.Element.Id);
        await AutoSaveAsync("move-element");
        await RefreshPreviewAsync();
    }

    private async void OnApplyTemplateJson(object? sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Template current) return;
        try
        {
            var parsed = JsonSerializer.Deserialize<Template>(TemplateJsonBox.Text ?? "", ProjectJson.Options);
            if (parsed is null) throw new InvalidOperationException("Template JSON is empty.");
            parsed.Id = current.Id; // preserve selection and instance references
            _api.UpdateTemplate(_project, parsed);
            RefreshTemplates();
            TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == parsed.Id);
            UpdateTemplateEditor();
            await AutoSaveAsync("update-template-json");
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Apply template JSON failed", ex);
            ExportStatus.Text = $"Template JSON error: {ex.Message}";
        }
    }

    private async void OnAddTemplate(object? sender, RoutedEventArgs e)
    {
        var t = new Template
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = "New Template",
            Width = 420,
            Height = 140,
            DefaultDurationMs = 3000,
            Animation = new Animation { Enter = AnimationStyle.SlideLeft, Exit = AnimationStyle.SlideLeft, EnterMs = 350, ExitMs = 350 },
            Elements = new List<Element>
            {
                new ShapeElement
                {
                    Id = "shape.bg",
                    OffsetX = 0, OffsetY = 0, Width = 420, Height = 140,
                    Shape = ShapeKind.Rectangle, Fill = Color.Black, BorderColor = Color.White, BorderThickness = 4, CornerRadius = 0
                },
                new TextElement
                {
                    Id = "text.main",
                    OffsetX = 16, OffsetY = 16, Width = 388, Height = 108,
                    FontFamily = "Segoe UI", FontSize = 34, Bold = false, Italic = false,
                    TextColor = Color.White, HAlign = HorizontalAlign.Left, VAlign = VerticalAlign.Top,
                    DefaultText = "New caption"
                }
            }
        };
        _api.CreateTemplate(_project, t);
        RefreshTemplates();
        TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == t.Id);
        await AutoSaveAsync("create-template");
    }

    private async void OnDuplicateTemplate(object? sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Template selected) return;
        var dup = _api.DuplicateTemplate(_project, selected.Id, $"{selected.Name} copy");
        RefreshTemplates();
        TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == dup.Id);
        await AutoSaveAsync("duplicate-template");
    }

    private async void OnDeleteTemplate(object? sender, RoutedEventArgs e)
    {
        if (TemplatesList.SelectedItem is not Template selected) return;
        try
        {
            _api.DeleteTemplate(_project, selected.Id);
            RefreshTemplates();
            UpdateTemplateEditor();
            await AutoSaveAsync("delete-template");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Delete template failed", ex);
            ExportStatus.Text = $"Delete template failed: {ex.Message}";
        }
    }

    // ── Hover action handlers ──────────────────────────────────────────────

    private async void OnTemplateItemDuplicate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var template = _project.Templates.FirstOrDefault(t => t.Id == id);
        if (template is null) return;
        var dup = _api.DuplicateTemplate(_project, id, $"{template.Name} copy");
        RefreshTemplates();
        TemplatesList.SelectedItem = Templates.FirstOrDefault(x => x.Id == dup.Id);
        await AutoSaveAsync("duplicate-template");
    }

    private async void OnTemplateItemDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        try
        {
            _api.DeleteTemplate(_project, id);
            RefreshTemplates();
            UpdateTemplateEditor();
            await AutoSaveAsync("delete-template");
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Delete template failed", ex);
            ExportStatus.Text = $"Delete template failed: {ex.Message}";
        }
    }

    private void OnInstanceItemPreview(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var inst = _project.Instances.FirstOrDefault(i => i.Id == id);
        if (inst is not null)
            TimeSlider.Value = inst.StartMs;
    }

    private async void OnInstanceItemDelete(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _api.DeleteInstance(_project, id);
        RefreshInstances();
        await AutoSaveAsync("delete-instance");
    }

    private void OnPreviewInstance(object? sender, RoutedEventArgs e)
    {
        if (SelectedInstance is { } inst)
            TimeSlider.Value = inst.StartMs;
    }

    private Task AutoSaveAsync(string reason)
    {
        if (_isApplyingProject) return Task.CompletedTask;
        try
        {
            _projectPath ??= UiSettingsStore.GetProjectPath(_project.Name);
            if (File.Exists(_projectPath))
            {
                var backupDir = UiSettingsStore.GetBackupDir(_projectPath);
                var backupFile = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.veproj.bak");
                File.Copy(_projectPath, backupFile, overwrite: false);
            }

            _api.SaveProject(_project, _projectPath);
            RememberRecentProject(_projectPath);
            CleanupBackups();
            ExportStatus.Text = $"Auto-saved ({reason})";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Auto-save failed", ex);
            ExportStatus.Text = $"Auto-save failed: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    private void CleanupBackups()
    {
        if (!_settings.AutoDeleteBackupsEnabled) return;
        try
        {
            var root = Path.Combine(UiSettingsStore.AppDataDir, "backups");
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, _settings.AutoDeleteBackupsDays));
            foreach (var file in Directory.GetFiles(root, "*.bak", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Backup cleanup failed", ex);
        }
    }

    private void LoadRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var path in _settings.RecentProjects.Where(File.Exists))
        {
            RecentProjects.Add(new RecentProjectItem
            {
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                CreatedLabel = RelativeTime(File.GetCreationTime(path)),
                UpdatedLabel = RelativeTime(File.GetLastWriteTime(path))
            });
        }
    }

    private static string RelativeTime(DateTime dt)
    {
        var elapsed = DateTime.Now - dt;
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min. ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hr. ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} days ago";
        if (elapsed.TotalDays < 30) return $"{(int)(elapsed.TotalDays / 7)} wk. ago";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)} mo. ago";
        return dt.ToString("yyyy-MM-dd");
    }

    private void RememberRecentProject(string path)
    {
        _settings.RecentProjects.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentProjects.Insert(0, path);
        if (_settings.RecentProjects.Count > 20) _settings.RecentProjects = _settings.RecentProjects.Take(20).ToList();
        UiSettingsStore.Save(_settings);
        LoadRecentProjects();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_project.VideoPath))
        {
            VideoInfoLabel.Text = "Open a video first.";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            DefaultExtension = "mp4",
            SuggestedFileName = "export.mp4"
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        var jobId = _api.StartExport(_project, new ExportOptions(path));
        ExportStatus.Text = $"Job {jobId[..8]}… running";
        _ = PollJob(jobId);
    }

    private async void OnExportSubtitles(object? sender, RoutedEventArgs e)
    {
        if (_project.Instances.Count == 0)
        {
            VideoInfoLabel.Text = "Add some captions first.";
            return;
        }

        // Show filter dialog: multi-select template names (or "All" by leaving none ticked)
        var templateNames = _project.Templates.Select(t => t.Name).Distinct().ToList();
        if (templateNames.Count == 0)
        {
            VideoInfoLabel.Text = "No templates available.";
            return;
        }

        var (cancelled, selectedTemplates) = await PromptMultiSelection(
            "Filter by template",
            "Tick templates to include (leave all unchecked to include every template):",
            templateNames);
        if (cancelled) return;

        IReadOnlyList<string>? templateFilters =
            selectedTemplates.Count == 0 || selectedTemplates.Count == templateNames.Count
                ? null
                : selectedTemplates;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Subtitles",
            DefaultExtension = "srt",
            SuggestedFileName = "captions.srt"
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        try
        {
            var ext = Path.GetExtension(path).ToLower().TrimStart('.');
            var format = ext switch
            {
                "srt" => "srt",
                "vtt" => "vtt",
                "json" => "json",
                _ => "srt"
            };
            var options = new ExportSubtitlesOptions(
                OutputPath: path,
                Format: format,
                TemplateTypeFilter: null,
                StartTimeMs: null,
                EndTimeMs: null,
                TemplateNameFilters: templateFilters);
            await _api.ExportSubtitlesAsync(_project, options);
            ExportStatus.Text = $"Subtitles exported → {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Subtitle export failed", ex);
            ExportStatus.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void OnExportCapCut(object? sender, RoutedEventArgs e)
    {
        if (_project.Instances.Count == 0)
        {
            VideoInfoLabel.Text = "Add at least one template instance first.";
            return;
        }

        var defaultFolder = !string.IsNullOrWhiteSpace(_settings.CapCutProjectsFolder)
            ? _settings.CapCutProjectsFolder
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CapCut", "User Data", "Projects", "com.lveditor.draft");

        IStorageFolder? startFolder = null;
        try
        {
            if (Directory.Exists(defaultFolder))
                startFolder = await StorageProvider.TryGetFolderFromPathAsync(defaultFolder);
        }
        catch { /* best-effort */ }

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the CapCut project folder to extend",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
        });
        var folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(folder)) return;

        // Persist the parent folder as the default for next time, so future exports start here.
        try
        {
            var parent = Path.GetDirectoryName(folder!);
            if (!string.IsNullOrEmpty(parent) && parent != _settings.CapCutProjectsFolder)
            {
                _settings.CapCutProjectsFolder = parent;
                UiSettingsStore.Save(_settings);
            }
        }
        catch { /* best-effort */ }

        if (!File.Exists(Path.Combine(folder!, "draft_content.json")))
        {
            ExportStatus.Text = "Selected folder is not a CapCut project (no draft_content.json).";
            return;
        }

        // Mode: clone (safe, default) or edit-in-place (creates .bak)
        var mode = await PromptChoice(
            "Export to CapCut (Preview)",
            $"Add {_project.Instances.Count} template instance(s) to this CapCut project?\n\n" +
            "• Clone project (recommended): copies the folder and edits the copy.\n" +
            "• Edit in place: writes a .bak then modifies draft_content.json.\n\n" +
            "Each emitted element will receive a 'Left Slide-In' entry animation.\n" +
            "Any content from a previous VideoEmpty export will be replaced (not duplicated).",
            new[] { "Clone project", "Edit in place", "Cancel" });
        if (mode is null || mode == "Cancel") return;

        var options = new CapCutExportOptions(
            ProjectFolder: folder!,
            Mode: mode == "Edit in place" ? CapCutExportMode.EditInPlace : CapCutExportMode.CloneProject);

        try
        {
            ExportStatus.Text = "Exporting to CapCut…";
            var result = await Task.Run(() => _api.ExportToCapCut(_project, options));
            var replaced = result.PreviousSegmentsRemoved > 0
                ? $", replaced {result.PreviousSegmentsRemoved} prior segment(s)"
                : "";
            ExportStatus.Text =
                $"CapCut export done → {Path.GetFileName(result.ProjectFolder)} " +
                $"({result.TextMaterialsAdded} text, {result.ShapeMaterialsAdded} shape, {result.SegmentsAdded} segments{replaced})";
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{result.ProjectFolder}\""); } catch { }
        }
        catch (Exception ex)
        {
            Log.Error("UI", "CapCut export failed", ex);
            ExportStatus.Text = $"CapCut export failed: {ex.Message}";
        }
    }

    private async Task<string?> PromptChoice(string title, string prompt, IReadOnlyList<string> choices)
    {
        var tcs = new TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttonRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        foreach (var c in choices)
        {
            var btn = new Button { Content = c };
            var captured = c;
            btn.Click += (_, _) => { tcs.TrySetResult(captured); dialog.Close(); };
            buttonRow.Children.Add(btn);
        }
        panel.Children.Add(buttonRow);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<(bool cancelled, List<string> selected)> PromptMultiSelection(string title, string prompt, List<string> options)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height
        };

        var cancelled = true;
        var checkBoxes = options.Select(opt => new CheckBox { Content = opt, IsChecked = true, Margin = new(0, 2) }).ToList();

        var grid = new Grid
        {
            ColumnDefinitions = new("*"),
            RowDefinitions = new("Auto,Auto,*,Auto"),
            Margin = new(16),
            RowSpacing = 10
        };

        var promptLabel = new TextBlock { Text = prompt, TextWrapping = AvaloniaMedia.TextWrapping.Wrap };
        Grid.SetRow(promptLabel, 0);
        grid.Children.Add(promptLabel);

        var selectionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var selectAllBtn = new Button { Content = "Select All" };
        var selectNoneBtn = new Button { Content = "Select None" };
        selectAllBtn.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        selectNoneBtn.Click += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };
        selectionButtons.Children.Add(selectAllBtn);
        selectionButtons.Children.Add(selectNoneBtn);
        Grid.SetRow(selectionButtons, 1);
        grid.Children.Add(selectionButtons);

        var listPanel = new StackPanel();
        foreach (var cb in checkBoxes) listPanel.Children.Add(cb);
        var scroller = new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = 320,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var okButton = new Button { Content = "OK", Width = 80, IsDefault = true };
        okButton.Click += (_, _) => { cancelled = false; dialog.Close(); };
        buttonPanel.Children.Add(okButton);
        var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        cancelButton.Click += (_, _) => { cancelled = true; dialog.Close(); };
        buttonPanel.Children.Add(cancelButton);
        Grid.SetRow(buttonPanel, 3);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        await dialog.ShowDialog(this);

        var selected = checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Content!).ToList();
        return (cancelled, selected);
    }

    private void OnTogglePanels(object? sender, RoutedEventArgs e)
    {
        // Toggle: if either panel is hidden, show both; otherwise hide both.
        if (_compactMode)
        {
            // In compact mode: cycle left visibility (right stays hidden)
            _showLeftPanel = !_showLeftPanel;
        }
        else
        {
            var anyHidden = !_showLeftPanel || !_showRightPanel;
            _showLeftPanel = anyHidden;
            _showRightPanel = anyHidden;
        }
        ApplyLayoutMode();
    }

    private void OnToggleCompactMode(object? sender, RoutedEventArgs e)
    {
        _compactMode = !_compactMode;
        if (_compactMode)
        {
            _showLeftPanel = false;
            _showRightPanel = false;
        }
        else
        {
            _showLeftPanel = true;
            _showRightPanel = true;
            HideCompactOverlay();
        }
        ApplyLayoutMode();
    }

    private void OnCollapseLeft(object? sender, RoutedEventArgs e)
    {
        _showLeftPanel = false;
        ApplyLayoutMode();
    }

    private void OnCollapseRight(object? sender, RoutedEventArgs e)
    {
        _showRightPanel = false;
        ApplyLayoutMode();
    }

    private void OnExpandLeftEdge(object? sender, RoutedEventArgs e)
    {
        _showLeftPanel = true;
        ApplyLayoutMode();
    }

    private void OnExpandRightEdge(object? sender, RoutedEventArgs e)
    {
        _showRightPanel = true;
        ApplyLayoutMode();
    }

    private void ApplyLayoutMode()
    {
        // Actually shrink the columns so the central video preview expands.
        // Column 0 = LeftTemplatePanel, 1 = splitter, 3 = splitter, 4 = RightPropertiesPanel.
        var cols = MainEditorGrid.ColumnDefinitions;
        cols[0].Width = _showLeftPanel ? new GridLength(360) : new GridLength(0);
        cols[1].Width = _showLeftPanel ? GridLength.Auto : new GridLength(0);
        cols[3].Width = _showRightPanel ? GridLength.Auto : new GridLength(0);
        cols[4].Width = _showRightPanel ? new GridLength(340) : new GridLength(0);

        LeftTemplatePanel.IsVisible = _showLeftPanel;
        RightPropertiesPanel.IsVisible = _showRightPanel;

        // Edge re-expand tabs are visible only inside the editor and only when the
        // adjacent panel is collapsed.
        var inEditor = EditorRoot.IsVisible;
        ExpandLeftEdgeButton.IsVisible = inEditor && !_showLeftPanel;
        ExpandRightEdgeButton.IsVisible = inEditor && !_showRightPanel;

        CompactTemplatesToolbar.IsVisible = _compactMode && !_showLeftPanel;

        CompactModeButton.Classes.Set("active", _compactMode);
        TogglePanelsButton.Classes.Set("active", !_showLeftPanel || !_showRightPanel);

        if (!_compactMode) HideCompactOverlay();
    }

    private void ShowCompactOverlayForCurrentInstance(Template template)
    {
        if (InstanceTextFields.Count == 0)
        {
            HideCompactOverlay();
            return;
        }
        CompactOverlayTitle.Text = $"{template.Name} — enter caption text";
        CompactInstanceOverlay.IsVisible = true;
        // Focus the first text box once the ListBox realizes its containers.
        TryFocusCompactOverlayTextBox(InstanceTextFields[0].ElementId, attemptsRemaining: 8);
    }

    private void HideCompactOverlay()
    {
        CompactInstanceOverlay.IsVisible = false;
    }

    private void OnCompactOverlayClose(object? sender, RoutedEventArgs e) => HideCompactOverlay();

    private void OnCompactOverlayExpand(object? sender, RoutedEventArgs e)
    {
        // Reveal full properties panel and hide the floating overlay.
        _showRightPanel = true;
        ApplyLayoutMode();
        HideCompactOverlay();
        FocusFirstInstanceTextFieldForReplace();
    }

    private TextBox? FindCompactOverlayTextBoxById(string elementId) =>
        CompactOverlayFieldsList
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.Tag is string id && id == elementId);

    private void TryFocusCompactOverlayTextBox(string elementId, int attemptsRemaining)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = FindCompactOverlayTextBoxById(elementId);
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
                return;
            }
            if (attemptsRemaining > 0)
                TryFocusCompactOverlayTextBox(elementId, attemptsRemaining - 1);
        }, DispatcherPriority.Background);
    }

    private async void OnCompactOverlayTextFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        await ApplyInstanceFromEditorAsync("update-instance-text");
    }

    private void OnCompactOverlayTextFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox current || e.Key != Key.Tab) return;
        if (current.Tag is not string currentId) return;

        var ids = InstanceTextFields.Select(f => f.ElementId).ToList();
        var index = ids.IndexOf(currentId);
        if (index < 0) return;

        var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
        var nextIndex = index + delta;
        if (nextIndex < 0 || nextIndex >= ids.Count) return;

        e.Handled = true;
        var nextId = ids[nextIndex];
        var nextBox = FindCompactOverlayTextBoxById(nextId);
        if (nextBox is null) return;
        nextBox.Focus();
        nextBox.SelectAll();
    }

    private async Task TogglePlaybackFromTextEntryAsync()
    {
        await ApplyInstanceFromEditorAsync("toggle-playback");
        TogglePlay();
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (EditorRoot.IsVisible &&
            e.Key == Key.Enter &&
            e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            await TogglePlaybackFromTextEntryAsync();
            return;
        }

        // Frequent timeline shortcuts: jump back/forward 10s without reaching for mouse.
        // Use Ctrl+Alt so Ctrl+Left / Ctrl+Shift+Left word navigation in textboxes is unaffected.
        if (EditorRoot.IsVisible &&
            e.Key == Key.Left &&
            e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            e.Handled = true;
            SeekRelative(-10000);
            return;
        }
        if (EditorRoot.IsVisible &&
            e.Key == Key.Right &&
            e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            e.Handled = true;
            SeekRelative(+10000);
        }
    }

    private void OnCompactTemplateButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string templateId) return;
        _armedTemplateId = templateId;
        var template = _api.GetTemplate(_project, templateId);
        ArmedLabel.Text = $"Armed: {template.Name} (click to add, Shift+click to retime latest)";
        RenderPlacementOverlay();
    }

    private async Task PollJob(string jobId)
    {
        while (true)
        {
            await Task.Delay(500);
            var s = _api.GetJobStatus(jobId);
            ExportStatus.Text = $"Job {jobId[..8]}: {s.State} ({s.Progress * 100:0}%) {s.Message}";
            if (s.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            {
                if (s.State == JobState.Failed) ExportStatus.Text += " — " + s.Error;
                else if (s.State == JobState.Completed) ExportStatus.Text = $"Done → {s.OutputPath}";
                break;
            }
        }
    }
}

/// <summary>Wraps a <see cref="TemplateInstance"/> with resolved display data for the instances list.</summary>
public sealed class InstanceListItem : INotifyPropertyChanged
{
    private string _templateName = string.Empty;
    private string _timeLabel = string.Empty;
    private string _textPreview = string.Empty;

    public required TemplateInstance Instance { get; init; }

    public required string TemplateName
    {
        get => _templateName;
        set { if (_templateName != value) { _templateName = value; OnChanged(nameof(TemplateName)); } }
    }

    public required string TimeLabel
    {
        get => _timeLabel;
        set { if (_timeLabel != value) { _timeLabel = value; OnChanged(nameof(TimeLabel)); } }
    }

    public string TextPreview
    {
        get => _textPreview;
        set { if (_textPreview != value) { _textPreview = value; OnChanged(nameof(TextPreview)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Display model for a recent project entry on the dashboard.</summary>
public sealed class RecentProjectItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string CreatedLabel { get; init; }
    public required string UpdatedLabel { get; init; }
}

/// <summary>Display row for the elements ListBox in the visual template editor.</summary>
public sealed class ElementListItem
{
    public required Element Element { get; init; }
    public required string Icon { get; init; }
    public required string Summary { get; init; }
}

/// <summary>Display row for an instance text value mapped to a template text element.</summary>
public sealed class InstanceTextFieldItem
{
    public required string ElementId { get; init; }
    public required string Label { get; init; }
    public string Value { get; set; } = "";
}
