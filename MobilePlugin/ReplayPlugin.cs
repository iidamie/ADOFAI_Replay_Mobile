using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace Replay.Mobile;

public sealed partial class ReplayPlugin : IModPlugin, IModSettings
{
    private const string LogTag = "Replay";
    private const int PlayerControlState = 4;
    private const int StableIdentityTicksRequired = 20;
    private const float ChartCoverRotationSpeedDegreesPerSecond = 30f;
    private const int ChartCoverSegmentCount = 64;
    private static readonly TimeSpan CustomLevelBrowserTimeout = TimeSpan.FromSeconds(60);

    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<ReplayCommand> _commands = new();
    private readonly HashSet<string> _autoSavedSessions = new(StringComparer.Ordinal);

    private GameApi? _game;
    private ReplayChartPreview? _replayChartPreview;
    private ReplayAudioPreview? _replayAudioPreview;
    private ReplayStore? _store;
    private GitHubUpdateService? _updateService;
    private ReplayData? _currentAttempt;
    private ReplayData? _lastAttempt;
    private ReplayData? _activeReplay;
    private ReplayData? _pendingReplay;
    private ReplayData? _resultAttempt;
    private ReplayRunState _runState;
    private ReplayLoadStage _loadStage;
    private List<ReplayFileEntry> _files = new();
    private nint _controller;
    private nint _player;
    private nint _pendingAttemptController;
    private int _replayIndex;
    private int _replayTouchIndex;
    private int _replayKeyboardIndex;
    private long _recordingStartTicks;
    private long _replayClockStartTicks;
    private long _replayPausedTicks;
    private long _replayPauseStartTicks;
    private int _pendingAttemptStartTile = -1;
    private int _identityStableTicks;
    private int _languageCode = 10;
    private ReplayLevelIdentity? _pendingIdentity;
    private string _lastScannedDirectory = "";
    private string _selectedReplayPath = "";
    private string _editingReplayPath = "";
    private string _replayTitleEdit = "";
    private bool _managerTitleEditing;
    private string _managerPreviewKey = "";
    private string _fileSearch = "";
    private string _pendingDeletePath = "";
    private bool _recording;
    private bool _levelTransitionInProgress;
    private bool _editorPlayRequested;
    private bool _editorFinalized;
    private bool _editorHooksInstalled;
    private bool _renderErrorLogged;
    private bool _loaded;
    private bool _customReplayExitRedirectPending;
    private bool _touchInputSubscribed;
    private bool _keyboardInputActive;
    private bool _managerOpen;
    private int _managerSection;
    private bool _replayControlsExpanded = true;
    private bool _replayDifficultySelectorShown;
    private bool _managerShowingDetails;
    private bool _managerPausedGame;
    private bool _resultAttemptSaved;
    private bool _resultSaveQueued;
    private DateTime _resultSaveButtonUntilUtc;
    private bool _deletePopupRequested;
    private bool _islandEntryLogged;
    private long _lastSettingsGuiTick;
    private string _notice = "";
    private DateTime _noticeUntilUtc;
    private string _toast = "";
    private DateTime _toastUntilUtc;
    private DateTime _loadDeadlineUtc;

    private bool _saveFullClear = true;
    private bool _saveEveryCompletion;
    private bool _saveEveryFailure;
    private bool _saveFailureAt90Percent = true;
    private bool _disableTutorialAutoSave = true;
    private bool _ignoreAutoplay = true;
    private bool _showReplayHud = true;
    private bool _receiveTouchInput = true;
    private bool _receiveKeyboardInput;
    private int _hudFontSize = 24;
    private float _hudPositionX = 0.02f;
    private float _hudPositionY = 0.08f;
    private int _maximumSavedReplays = 100;
    private string _replayDirectory = "";
    private int _inputDisplayWidth;
    private int _inputDisplayHeight;

    public string Id => "Replay";
    public string Name => "ADOFAI Replay";
    public string Version => ModVersion;
    public string Author => "Flower / ADOFAI.gg";
    public string Description => "Record and replay ADOFAI mobile runs with IL2CPP-native hooks";
    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    /// <summary>
    /// 从程序集元数据读取版本号，保证与 Replay.csproj 的 &lt;Version&gt; 始终一致，
    /// 不会再出现「csproj 已升级但插件里仍是旧硬编码字符串」的情况。
    /// </summary>
    internal static string ModVersion { get; } = ResolveModVersion();

    private static string ResolveModVersion()
    {
        string? informational = typeof(ReplayPlugin).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return typeof(ReplayPlugin).Assembly.GetName().Version?.ToString() ?? "1.5.1";
        int metadataSeparator = informational.IndexOf('+');
        return metadataSeparator < 0 ? informational : informational[..metadataSeparator];
    }

    internal bool IsReplayActive
    {
        get
        {
            lock (_stateLock)
                return _activeReplay != null;
        }
    }

    internal bool IsReplayLoadPending
    {
        get
        {
            lock (_stateLock)
                return _pendingReplay != null;
        }
    }

    internal bool IsReplayTargetStartPending
    {
        get
        {
            lock (_stateLock)
                return _pendingReplay != null
                    && _loadStage == ReplayLoadStage.WaitingForTargetStart;
        }
    }

    public void OnLoad()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string modDirectory = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        try
        {
            _store = new ReplayStore(modDirectory);
            LoadSettings();
            _game = GameApi.Create();
            if (_game == null)
                throw new InvalidOperationException("ADOFAI IL2CPP runtime or Assembly-CSharp was not found");
            _replayChartPreview = new ReplayChartPreview(_game.RuntimeDomain, _game.GameAssembly);
            _replayAudioPreview = new ReplayAudioPreview(_game.RuntimeDomain, _game.GameAssembly);

            Logger.Info(
                LogTag,
                $"Runtime custom-level loader available: {_game.CanLoadScenes}");
            _languageCode = _game.GetLanguageCode();
            if (!GameHooks.Install(this, _game))
                throw new InvalidOperationException("Required Replay IL2CPP hooks could not be installed");
            _loaded = true;
            SyncInputReceivers();
            CustomLoadDiagnostics.Install(this);
            _updateService = new GitHubUpdateService(modDirectory, Version);
            _updateService.StartAutomaticCheck();

            RefreshFiles();
            nint controller = _game.GetController();
            if (_game.IsGameWorld(controller))
                QueueAttemptStart(controller, _game.GetCurrentSequence(controller));
            Logger.Info(
                LogTag,
                $"Loaded for StArray.ModManager 1.0.4+; version={Version}");
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Replay load failed; rolling back initialization: {exception}");
            RollbackLoad();
            throw;
        }
    }

    private void RollbackLoad()
    {
        _loaded = false;
        TryRollbackLoadStep("input receivers", SyncInputReceivers);
        TryRollbackLoadStep("updater", () =>
        {
            try
            {
                _updateService?.Dispose();
            }
            finally
            {
                _updateService = null;
            }
        });
        TryRollbackLoadStep("keyboard hook", ReplayKeyboardHook.Uninstall);
        TryRollbackLoadStep("keyboard recorder", ReplayKeyboardRecorder.Reset);
        TryRollbackLoadStep("replay input playback", EndReplayInputPlayback);
        TryRollbackLoadStep("editor hooks", EditorSupport.Uninstall);
        TryRollbackLoadStep("custom load diagnostics", CustomLoadDiagnostics.Uninstall);
        TryRollbackLoadStep("game hooks", GameHooks.Uninstall);
        TryRollbackLoadStep("preview audio", () =>
        {
            try
            {
                _replayAudioPreview?.Dispose();
            }
            finally
            {
                _replayAudioPreview = null;
            }
        });
        TryRollbackLoadStep("preview chart", () =>
        {
            try
            {
                _replayChartPreview?.Dispose();
            }
            finally
            {
                _replayChartPreview = null;
            }
        });
        _commands.Clear();

        lock (_stateLock)
        {
            _game = null;
            _store = null;
            _currentAttempt = null;
            _lastAttempt = null;
            _activeReplay = null;
            _pendingReplay = null;
            _resultAttempt = null;
            _files.Clear();
            _controller = 0;
            _player = 0;
            _recording = false;
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
            _levelTransitionInProgress = false;
            _editorPlayRequested = false;
            _editorFinalized = false;
            _editorHooksInstalled = false;
            _customReplayExitRedirectPending = false;
            _touchInputSubscribed = false;
            _keyboardInputActive = false;
            _managerOpen = false;
            _managerShowingDetails = false;
            _managerPausedGame = false;
            _resultAttemptSaved = false;
            _resultSaveQueued = false;
            _resultSaveButtonUntilUtc = default;
            _autoSavedSessions.Clear();
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
            _recordingStartTicks = 0;
            _replayClockStartTicks = 0;
            _replayPausedTicks = 0;
            _replayPauseStartTicks = 0;
        }
    }

    private void TryRollbackLoadStep(string stage, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Replay load rollback failed at {stage}: {exception}");
        }
    }

    public void OnUnload()
    {
        _loaded = false;
        SyncInputReceivers();
        _updateService?.Dispose();
        _updateService = null;
        ReplayKeyboardHook.Uninstall();
        ReplayKeyboardRecorder.Reset();
        EndReplayInputPlayback();
        SaveSettings();
        CloseReplayManager();
        try
        {
            StopPlaybackNow();
        }
        catch
        {
        }
        try
        {
            _replayAudioPreview?.Dispose();
            _replayChartPreview?.Dispose();
        }
        catch
        {
        }
        _replayAudioPreview = null;
        _replayChartPreview = null;
        // Detach is used for scene changes, but unloading the mod must always
        // remove the native editor detours as well.
        EditorSupport.Uninstall();
        _editorHooksInstalled = false;
        CustomLoadDiagnostics.Uninstall();
        GameHooks.Uninstall();
        lock (_stateLock)
        {
            _game = null;
            _store = null;
            _currentAttempt = null;
            _activeReplay = null;
            _pendingReplay = null;
            _resultAttempt = null;
            _recording = false;
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
            _levelTransitionInProgress = false;
            _editorPlayRequested = false;
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
            _resultAttemptSaved = false;
            _resultSaveQueued = false;
            _resultSaveButtonUntilUtc = default;
            _replayTouchIndex = 0;
            _replayKeyboardIndex = 0;
            _recordingStartTicks = 0;
            _replayClockStartTicks = 0;
            _replayPausedTicks = 0;
            _replayPauseStartTicks = 0;
            _replayDifficultySelectorShown = false;
            _toast = "";
            _toastUntilUtc = default;
        }
        Logger.Info(LogTag, "Unloaded");
    }

    public void OnForegroundGUI(ImDrawListPtr drawList)
    {
        TryRenderForeground("input receivers", SyncInputReceivers);
        if (_keyboardInputActive)
            TryRenderForeground("keyboard input", () => ReplayKeyboardRecorder.Update(this));
        TryRenderForeground(
            "update notification",
            () => _updateService?.DrawForegroundNotification());
        TryRenderForeground("input display size", () =>
        {
            Vector2 display = ImGui.GetIO().DisplaySize;
            if (display.X > 1f && display.Y > 1f)
            {
                Volatile.Write(ref _inputDisplayWidth, Math.Max(0, (int)MathF.Round(display.X)));
                Volatile.Write(ref _inputDisplayHeight, Math.Max(0, (int)MathF.Round(display.Y)));
            }
        });

        // Keep independent overlays alive if a transient native scene object
        // is unavailable during a 3.1.2/3.3.1 transition. Previously one
        // exception set _renderErrorLogged and returned from every later
        // frame, which made the home entry appear once and then vanish.
        TryRenderForeground("replay HUD", () => DrawHud(drawList));
        TryRenderForeground("replay controls", DrawReplayControls);
        TryRenderForeground("result save button", DrawResultSaveButton);
        TryRenderForeground("main entry", DrawIslandEntry);
        TryRenderForeground("replay manager", DrawReplayManager);
        TryRenderForeground("toast", DrawToast);
    }

    private void TryRenderForeground(string section, Action draw)
    {
        try
        {
            draw();
        }
        catch (Exception exception)
        {
            if (_renderErrorLogged)
                return;
            _renderErrorLogged = true;
            Logger.Error(LogTag, $"Foreground {section} render failed: {exception}");
        }
    }

    private void OnTouch(TouchEventInfo info)
    {
        if (!_receiveTouchInput)
            return;
        lock (_stateLock)
        {
            if (!_recording || _currentAttempt == null || _managerOpen)
                return;

            int pointerId = info.Action == AndroidInput.MotionAction.Cancel
                ? -1
                : info.PointerId >= 0 ? info.PointerId : info.PointerIndex;
            _currentAttempt.TouchEvents.Add(new ReplayTouchInput
            {
                TimeMilliseconds = GetElapsedMilliseconds(_recordingStartTicks),
                Action = (int)info.Action,
                PointerId = pointerId,
                X = info.X,
                Y = info.Y,
                SourceWidth = Volatile.Read(ref _inputDisplayWidth),
                SourceHeight = Volatile.Read(ref _inputDisplayHeight),
            });
        }
    }

    internal bool IsRecordingKeyboard
    {
        get
        {
            lock (_stateLock)
                return _receiveKeyboardInput
                    && _recording && _currentAttempt != null && !_managerOpen;
        }
    }

    internal void CaptureNativeKeyboardEvent(IntPtr inputEvent)
    {
        ReplayKeyboardRecorder.CaptureNativeEvent(this, inputEvent);
    }

    internal void RecordKeyboardInput(string binding, int action, int repeat = 0)
    {
        if (!_receiveKeyboardInput || string.IsNullOrWhiteSpace(binding))
            return;
        lock (_stateLock)
        {
            if (!_recording || _currentAttempt == null || _managerOpen)
                return;
            _currentAttempt.KeyboardEvents ??= new List<ReplayKeyboardInput>();
            _currentAttempt.KeyboardEvents.Add(new ReplayKeyboardInput
            {
                TimeMilliseconds = GetElapsedMilliseconds(_recordingStartTicks),
                Binding = binding,
                Action = action,
                Repeat = Math.Max(0, repeat),
            });
        }
    }

    public void OnGui()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.10f, 0.22f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.14f, 0.32f, 0.31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.08f, 0.14f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.12f, 0.24f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.16f, 0.32f, 0.30f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.20f, 0.21f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.52f, 0.48f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.10f, 0.68f, 0.60f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.20f, 0.82f, 0.72f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 9f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
        Interlocked.Exchange(ref _lastSettingsGuiTick, Environment.TickCount64);
        NormalizeSettings();
        // UI callbacks can run on ModManager's managed/render thread. Native
        // scene transitions, especially LoadCustomLevel, must be initiated
        // from Replay's controller/conductor hooks on the Unity game thread.
        // The queued command is consumed by TickMainThread there.
        UiText ui = UiText.FromLanguage(_languageCode);
        if (!string.Equals(_lastScannedDirectory, _replayDirectory, StringComparison.Ordinal))
            RefreshFiles();

        string status = GetStatusText(ui, includeSong: true);
        if (!string.IsNullOrEmpty(status))
            ImGui.TextWrapped(status);
        ShowNotice();

        // 编辑器内不提供打开回放管理器的快捷按钮；编辑器的播放/编辑
        // 控件由游戏自身处理，避免在编辑器画布上叠加 Replay 入口。
        if (_game?.IsEditorScene() != true)
        {
            ImGui.Separator();
            if (ImGui.Button(ui.OpenManager, new Vector2(-1f, GetOverlayButtonHeight())))
                OpenReplayManager();

            ImGui.Separator();
        }
        ImGui.TextUnformatted(ui.SaveOptions);
        bool settingsChanged = false;
        settingsChanged |= ImGui.Checkbox(ui.SaveFullClear, ref _saveFullClear);
        settingsChanged |= ImGui.Checkbox(ui.SaveEveryCompletion, ref _saveEveryCompletion);
        settingsChanged |= ImGui.Checkbox(ui.SaveEveryFailure, ref _saveEveryFailure);
        settingsChanged |= ImGui.Checkbox(ui.SaveFailureAt90Percent, ref _saveFailureAt90Percent);
        settingsChanged |= ImGui.Checkbox(ui.DisableTutorialAutoSave, ref _disableTutorialAutoSave);
        settingsChanged |= ImGui.Checkbox(ui.DisableAutoReplay, ref _ignoreAutoplay);
        settingsChanged |= ImGui.Checkbox(ui.ShowHud, ref _showReplayHud);
        settingsChanged |= ImGui.Checkbox(ui.ReceiveTouchInput, ref _receiveTouchInput);
        settingsChanged |= ImGui.Checkbox(ui.ReceiveKeyboardInput, ref _receiveKeyboardInput);
        if (_showReplayHud)
        {
            settingsChanged |= ImGui.SliderInt(ui.HudSize, ref _hudFontSize, 12, 64);
            settingsChanged |= ImGui.SliderFloat("X", ref _hudPositionX, 0f, 1f, "%.2f");
            settingsChanged |= ImGui.SliderFloat("Y", ref _hudPositionY, 0f, 1f, "%.2f");
        }
        settingsChanged |= ImGui.SliderInt(ui.MaxFiles, ref _maximumSavedReplays, 1, 500);
        settingsChanged |= ImGui.InputText(ui.Directory, ref _replayDirectory, 512);
        if (settingsChanged)
        {
            NormalizeSettings();
            SyncInputReceivers();
            SaveSettings();
        }

        ImGui.Separator();
        if (ImGui.Button(ui.SaveCurrent, new Vector2(-1f, GetOverlayButtonHeight())))
            QueueSaveCurrent(ui);
        if (ImGui.Button(ui.PlayLast, new Vector2(-1f, GetOverlayButtonHeight())))
            QueuePlayLast(ui);

        ReplayRunState runState;
        lock (_stateLock)
            runState = _runState;
        if (runState is ReplayRunState.Playing or ReplayRunState.Paused
            or ReplayRunState.WaitingForStart or ReplayRunState.Finished or ReplayRunState.Failed)
        {
            if (runState is ReplayRunState.Playing or ReplayRunState.Paused)
            {
                if (ImGui.Button(runState == ReplayRunState.Paused ? ui.Resume : ui.Pause, new Vector2(-1f, GetOverlayButtonHeight())))
                    _commands.Enqueue(new ReplayCommand(ReplayCommandKind.TogglePause));
            }
            if (ImGui.Button(ui.Stop, new Vector2(-1f, GetOverlayButtonHeight())))
                _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
        }
        _updateService?.DrawGui();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(9);
    }

    internal bool ShouldBlockPlayerHit(nint player)
    {
        lock (_stateLock)
        {
            if (_managerOpen || _activeReplay == null)
                return _managerOpen;

            return true;
        }
    }

    internal bool ShouldBlockInput()
    {
        lock (_stateLock)
        {
            return _managerOpen
                || _activeReplay != null
                    && (_runState == ReplayRunState.Playing
                        || _runState == ReplayRunState.Paused
                        || _runState == ReplayRunState.Finished
                        || _runState == ReplayRunState.Failed);
        }
    }

    internal PendingHit? BeginHit(nint player, bool autoHitArgument)
    {
        GameApi? game = _game;
        if (game == null || player == 0)
            return null;

        lock (_stateLock)
        {
            if (!_recording || _activeReplay != null || _currentAttempt == null)
                return null;
        }

        nint controller = game.GetController();
        if (!game.IsGameWorld(controller))
            return null;
        nint playerOne = game.GetPlayer(controller);
        if (playerOne != 0 && player != playerOne)
            return null;
        nint floor = game.GetCurrentFloor(player);
        if (floor == 0)
            return null;
        nint planet = game.GetChosenPlanet(player);
        if (planet == 0)
            return null;

        bool autoHit = autoHitArgument || game.IsPlayerAuto(player) || game.IsAutoNextFloor(floor);
        if (_ignoreAutoplay && game.IsPlayerAuto(player))
            return null;

        int sequenceId = game.GetCurrentSequence(controller);
        double angleOffset = game.GetPlanetAngle(planet) - game.GetTargetExitAngle(planet);
        bool noFailHit = game.IsNoFailInfinite(controller, player);
        int hitMargin = game.CalculateHitMargin(player, floor, planet);
        PendingHit pending;
        bool firstHit;
        lock (_stateLock)
        {
            if (!_recording || _currentAttempt == null || _activeReplay != null)
                return null;
            int hitIndex = _currentAttempt.Hits.Count;
            _currentAttempt.Hits.Add(new ReplayHit
            {
                SequenceId = sequenceId,
                HitAngleOffset = angleOffset,
                HitMargin = hitMargin,
                NoFailHit = noFailHit,
                AutoHit = autoHit,
            });
            _currentAttempt.EndTile = Math.Max(_currentAttempt.EndTile, sequenceId);
            pending = new PendingHit(sequenceId, hitIndex);
            firstHit = hitIndex == 0;
        }
        if (firstHit)
            Logger.Info(LogTag, $"Captured first hit at tile {sequenceId}");
        return pending;
    }

    internal void CompleteHit(PendingHit pending, int? hitMargin)
    {
        lock (_stateLock)
        {
            if (_currentAttempt == null
                || pending.HitIndex < 0
                || pending.HitIndex >= _currentAttempt.Hits.Count)
                return;

            ReplayHit hit = _currentAttempt.Hits[pending.HitIndex];
            if (hit.SequenceId != pending.SequenceId)
                return;
            if (hitMargin.HasValue)
                hit.HitMargin = hitMargin.Value;
        }
    }

















    private static ReplayHit CloneHit(ReplayHit source)
        => new()
        {
            SequenceId = source.SequenceId,
            HitAngleOffset = source.HitAngleOffset,
            HitMargin = source.HitMargin,
            NoFailHit = source.NoFailHit,
            AutoHit = source.AutoHit,
        };

    internal int GetStartTile(int requestedSequence)
    {
        lock (_stateLock)
        {
            ReplayData? replay = _activeReplay ?? _pendingReplay;
            bool targetLevelStarting = _activeReplay != null
                || _loadStage == ReplayLoadStage.WaitingForTargetStart;
            return replay != null
                && targetLevelStarting
                && _runState is not ReplayRunState.Finished and not ReplayRunState.Failed
                ? replay.StartTile
                : requestedSequence;
        }
    }

    /// <summary>
    /// 把待播放回放的起始砖重新写回 <c>GCS.checkpointNum</c>。
    /// scrController.Awake 会在场景切换时清零该字段（previousScene 变化、以及
    /// gameworld/speedTrialMode 分支），而 WaitForStartCo 又靠它把行星
    /// <c>ScrubToFloorNumber</c> 到起点、FinishCustomLevelLoading 靠它设置 currentSeqID。
    /// 因此从检查点录制的回放必须在关卡真正开始前把该值补回去，否则会从第 0 砖开始播放。
    /// </summary>
    internal void RestoreReplayCheckpoint()
    {
        GameApi? game = _game;
        if (game == null)
            return;

        int startTile;
        lock (_stateLock)
        {
            ReplayData? replay = _pendingReplay ?? _activeReplay;
            if (replay == null
                || _runState is ReplayRunState.Finished or ReplayRunState.Failed)
                return;
            // 回放已经开始推进后就不要再改写 checkpointNum，避免干扰游戏自身的检查点逻辑。
            if (_activeReplay != null && _runState is not ReplayRunState.WaitingForStart)
                return;
            startTile = replay.StartTile;
        }
        if (startTile <= 0 || game.GetCheckpoint() == startTile)
            return;
        game.SetCheckpoint(startTile);
    }

    internal void HandleStartRewind(nint controller, int sequenceId)
    {
        if (_game == null)
            return;
        GameApi game = _game;

        _controller = controller;
        _levelTransitionInProgress = false;
        _editorFinalized = false;

        if (_game.IsEditorScene())
            ArmEditorRecordingFromStartRewind();

        bool activated = false;
        bool restarted = false;
        ReplayData? playbackToStart = null;

        lock (_stateLock)
        {
            if (_pendingReplay != null
                && _loadStage == ReplayLoadStage.WaitingForTargetStart)
            {
                _activeReplay = _pendingReplay;
                _pendingReplay = null;
                _loadStage = ReplayLoadStage.None;
                _loadDeadlineUtc = default;
                _replayIndex = 0;
                _recording = false;
                _runState = ReplayRunState.WaitingForStart;
                playbackToStart = _activeReplay;
                activated = true;
            }
            else if (_activeReplay != null
                && _runState is not ReplayRunState.Finished and not ReplayRunState.Failed)
            {
                _replayIndex = 0;
                _recording = false;
                _runState = ReplayRunState.WaitingForStart;
                playbackToStart = _activeReplay;
                restarted = true;
            }
            else if (_runState is ReplayRunState.Finished or ReplayRunState.Failed)
            {
                _activeReplay = null;
                _runState = ReplayRunState.Idle;
            }
        }

        if (activated)
        {
            game.CancelPendingCustomReplayLoad();
            if (playbackToStart != null)
                game.SetDifficulty(playbackToStart.Difficulty);
            BeginReplayInputPlayback();
            Logger.Info(LogTag, "Replay activated after level load");
            return;
        }
        if (restarted)
        {
            if (playbackToStart != null)
                game.SetDifficulty(playbackToStart.Difficulty);
            BeginReplayInputPlayback();
            return;
        }

        if (QueueAttemptStart(controller, sequenceId))
            SetRecordingAnchor(Stopwatch.GetTimestamp());
    }

    private void ArmEditorRecordingFromStartRewind()
    {
        lock (_stateLock)
        {
            // A pending/active replay owns this Start_Rewind event. Do not
            // turn an editor replay playback into a fresh recording attempt.
            if (_activeReplay != null || _pendingReplay != null)
                return;

            _editorPlayRequested = true;
            _editorFinalized = false;
            _levelTransitionInProgress = false;
            _currentAttempt = null;
            _recording = false;
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
        }
        Logger.Info(
            LogTag,
            "Editor Start_Rewind received; editor recording armed without playMode polling");
    }

    internal void HandleLevelLoadStarted(nint controller)
    {
        EndReplayInputPlayback();
        ReplayData? discarded;
        lock (_stateLock)
        {
            discarded = _currentAttempt;
            _currentAttempt = null;
            _recording = false;
            _recordingStartTicks = 0;
            _pendingAttemptController = controller;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
            _levelTransitionInProgress = true;
            if (_activeReplay == null && _pendingReplay == null)
                _runState = ReplayRunState.Idle;
        }
        if (discarded is { Hits.Count: > 0 })
            Logger.Info(LogTag, $"Discarded unfinished recording during level transition: {discarded.SongName}");
    }

    internal void HandleEditorPlay(nint editorInstance)
    {
        int replayStartTile = -1;
        lock (_stateLock)
        {
            if (IsEditorReplay(_pendingReplay)
                && _loadStage == ReplayLoadStage.WaitingForTargetStart)
                replayStartTile = _pendingReplay!.StartTile;
            _editorPlayRequested = true;
            _editorFinalized = false;
            // Break the _levelTransitionInProgress deadlock in case the editor
            // caused a controller change but never calls Start_Rewind.
            _levelTransitionInProgress = false;
            // Abandon any stale attempt data so a fresh recording starts cleanly.
            _currentAttempt = null;
            _recording = false;
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
        }
        if (replayStartTile >= 0)
        {
            bool prepared = _game?.PrepareEditorReplayStart(editorInstance, replayStartTile) == true;
            Logger.Info(
                LogTag,
                prepared
                    ? $"Prepared editor replay start at tile {replayStartTile}"
                    : $"Could not prepare editor replay start at tile {replayStartTile}");
        }
        Logger.Info(LogTag, "Editor play started — recording will begin when controller is ready");
    }

    internal void HandleEditorReset()
    {
        lock (_stateLock)
        {
            _editorPlayRequested = false;
            _editorFinalized = false;
            // 从编辑器游玩返回预览时，当前回放可能已经消费了一部分输入。
            // 重新武装为待播放状态，保证下一次按 Play 必定从第 0 条开始。
            if (_activeReplay != null
                && _runState is ReplayRunState.WaitingForStart
                    or ReplayRunState.Playing
                    or ReplayRunState.Paused)
            {
                _pendingReplay = _activeReplay;
                _activeReplay = null;
                _replayIndex = 0;
                _runState = ReplayRunState.Loading;
                _loadStage = ReplayLoadStage.WaitingForTargetStart;
                _loadDeadlineUtc = default;
            }
            _currentAttempt = null;
            _recording = false;
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
        }
        EndReplayInputPlayback();
        Logger.Info(LogTag, "Editor reset — cleared editor play state");
    }

    private void UpdateEditorHooks()
    {
        bool inEditor = _game?.IsEditorScene() == true;
        if (inEditor && !_editorHooksInstalled)
        {
            EditorSupport.Install(this);
            _editorHooksInstalled = true;
            Logger.Info(LogTag, "Editor hooks installed (entered editor scene)");
        }
        else if (!inEditor && _editorHooksInstalled)
        {
            // Keep the native editor detours installed while the mod remains
            // loaded. Only detach this plugin instance between scene visits;
            // OnUnload/RollbackLoad performs the real unhook.
            EditorSupport.Detach();
            _editorHooksInstalled = false;
            if (_editorPlayRequested)
                HandleEditorReset();
            Logger.Info(LogTag, "Editor hooks uninstalled (left editor scene)");
        }
    }


    internal void TickMainThread(nint controller)
    {
        UpdateEditorHooks();
        DetectLevelTransition(controller);
        if (controller != 0)
            _controller = controller;
        // 关卡加载期间 scrController.Awake 会清零 checkpointNum，这里在游戏主线程持续补回，
        // 直到回放真正开始推进为止。
        RestoreReplayCheckpoint();
        _languageCode = _game?.GetLanguageCode() ?? _languageCode;
        while (_commands.TryDequeue(out ReplayCommand? command))
        {
            switch (command.Kind)
            {
                case ReplayCommandKind.SaveCurrent:
                    SaveCurrentNow();
                    break;
                case ReplayCommandKind.SaveResult:
                    SaveResultNow();
                    break;
                case ReplayCommandKind.Play when command.Replay != null:
                    StartPlaybackNow(command.Replay);
                    break;
                case ReplayCommandKind.Stop:
                    StopPlaybackNow();
                    break;
                case ReplayCommandKind.TogglePause:
                    TogglePauseNow();
                    break;
                case ReplayCommandKind.PauseForManager:
                    PauseForReplayManagerNow();
                    break;
                case ReplayCommandKind.ResumeAfterManager:
                    ResumeAfterReplayManagerNow();
                    break;
                case ReplayCommandKind.StartPreview when command.AudioPreview != null:
                    StartReplayAudioPreviewNow(command.AudioPreview);
                    break;
                case ReplayCommandKind.StopPreview:
                    StopReplayAudioPreviewNow();
                    break;
                case ReplayCommandKind.SetOfficialPreview:
                    _replayChartPreview?.SetOfficialLevel(command.OfficialLevelId);
                    break;
            }
        }
        SyncReplayDifficultySelector(_game);
        TickReplayAudioPreview();
        AdvancePendingReplayLoad();
        EnsureAttemptStarted(controller);
    }

    private void SyncReplayDifficultySelector(GameApi? game)
    {
        if (game == null)
            return;

        bool replayPending;
        lock (_stateLock)
        {
            replayPending = _activeReplay != null || _pendingReplay != null;
        }

        if (!replayPending)
        {
            if (_replayDifficultySelectorShown)
                MinimizeReplayDifficultySelector(game);
            return;
        }

        // The native Show -> Minimize sequence initializes the original text
        // and difficulty image, then leaves the compact status-only control.
        // It is intentionally independent of Loading/Waiting/Playing state;
        // the compact control is not the interactive pre-start panel.
        if (!_replayDifficultySelectorShown)
            ShowReplayDifficultyCompact(game);
    }

    private void ShowReplayDifficultyCompact(GameApi game)
    {
        if (!game.ShowReplayDifficultyCompact())
            return;
        _replayDifficultySelectorShown = true;
    }

    private void MinimizeReplayDifficultySelector(GameApi game)
    {
        if (!game.MinimizeReplayDifficultySelector())
            return;
        _replayDifficultySelectorShown = false;
    }





    private void AdvancePendingReplayLoad()
    {
        GameApi? game = _game;
        ReplayData? replay;
        DateTime deadline;
        ReplayLoadStage stage;
        lock (_stateLock)
        {
            if (_pendingReplay == null
                || _loadStage is not (ReplayLoadStage.WaitingForCustomLevelBrowser
                    or ReplayLoadStage.WaitingForLevelSelect))
                return;
            replay = _pendingReplay;
            deadline = _loadDeadlineUtc;
            stage = _loadStage;
        }

        if (deadline != default && DateTime.UtcNow >= deadline)
        {
            FailPendingReplayLoad(stage == ReplayLoadStage.WaitingForLevelSelect
                ? "等待游戏返回开始岛超时，请手动回到开始岛后重试。"
                : "等待游戏扫描自定义关卡列表超时，请确认谱面仍位于游戏的 Levels 目录中。");
            return;
        }

        CustomReplayLoadStatus status = (stage == ReplayLoadStage.WaitingForLevelSelect
                ? game?.AdvanceOfficialReplayLoad(replay)
                : game?.AdvanceCustomReplayLoad(replay))
            ?? CustomReplayLoadStatus.Failed;
        if (status == CustomReplayLoadStatus.Waiting)
            return;
        if (status == CustomReplayLoadStatus.Failed)
        {
            string error = string.IsNullOrWhiteSpace(game?.LastLoadError)
                ? UiText.FromLanguage(_languageCode).LoadFailed
                : game!.LastLoadError;
            FailPendingReplayLoad(error);
            return;
        }

        lock (_stateLock)
        {
            if (!ReferenceEquals(_pendingReplay, replay))
                return;
            _loadStage = ReplayLoadStage.WaitingForTargetStart;
            _loadDeadlineUtc = default;
            _customReplayExitRedirectPending = IsCustomReplayExitCandidate(replay);
        }
        Logger.Info(LogTag, $"Replay custom level opened via {game?.LastLoadRoute}: {game?.GetReplayLoadState()}");
    }





    private void FailPendingReplayLoad(string error)
    {
        EndReplayInputPlayback();
        if (_game != null)
            MinimizeReplayDifficultySelector(_game);
        _game?.CancelPendingCustomReplayLoad();
        lock (_stateLock)
        {
            _activeReplay = null;
            _pendingReplay = null;
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
            _customReplayExitRedirectPending = false;
        }
        SetNotice(error);
        Logger.Error(LogTag, $"Could not load replay level: {error}");
    }

    private void DetectLevelTransition(nint controller)
    {
        GameApi? game = _game;
        if (game == null || controller == 0)
            return;

        bool shouldHandle;
        lock (_stateLock)
        {
            bool controllerChanged = _controller != 0 && _controller != controller;
            shouldHandle = !_levelTransitionInProgress
                && (controllerChanged || game.IsLevelTransitioning(controller));
        }
        if (shouldHandle)
            HandleLevelLoadStarted(controller);
    }

    internal void TickConductorMainThread()
    {
        GameApi? game = _game;
        if (game != null)
            TickMainThread(game.GetController());
    }

    internal void TickPlayback()
    {
        GameApi? game = _game;
        if (game == null)
            return;

        nint controller = game.GetController();

        ReplayData? replay;
        ReplayRunState state;
        lock (_stateLock)
        {
            replay = _activeReplay;
            state = _runState;
        }
        if (replay == null || state is ReplayRunState.Idle or ReplayRunState.Loading
            or ReplayRunState.Paused or ReplayRunState.Finished or ReplayRunState.Failed)
            return;

        // A speed change rebuilds the native level through Replay's own
        // Start_Rewind flow. PlayerControl may briefly report state 4 while
        // the native Ready prompt is still waiting for the user's tap; do not
        // let Replay consume network events during that transient window.
        nint player = game.GetPlayer(controller);
        // 编辑器模式下 player 和 state 均不可靠，全部跳过。
        bool editorReplay;
        lock (_stateLock)
            editorReplay = IsEditorReplay(replay);
        if (!game.IsGameWorld(controller))
            return;
        // Start_Rewind 在 scnEditor.Play() 内部触发，但编辑器预览页的 controller
        // 也长期保持 gameworld=true。必须用游戏自己的 playMode 开闸，
        // 避免回放尚未真正开始时提前吞掉输入。
        if (editorReplay && !game.IsEditorPlayMode())
            return;
        if (!editorReplay)
        {
            if (player == 0)
                return;
            int controllerState = game.GetControllerState(controller);
            if (controllerState != PlayerControlState)
                return;
        }
        if (game.IsPaused(controller))
        {
            PauseReplayClock();
            return;
        }
        ResumeReplayClock();
        _controller = controller;
        _player = player;
        // scrConductor.Rewind clears hasSongStarted and the conductor sets it
        // only when the song really begins. Use it for both normal and editor
        // replays so input injection never consumes events during the
        // ready/countdown phase.
        if (!game.HasSongStarted())
            return;
        if (state == ReplayRunState.WaitingForStart)
        {
            ShowReplayDifficultyCompact(game);
            lock (_stateLock)
            {
                _runState = ReplayRunState.Playing;
                if (_replayClockStartTicks == 0)
                    _replayClockStartTicks = Stopwatch.GetTimestamp();
                state = _runState;
            }
        }
        PublishDueReplayInputEvents(replay);

        for (int count = 0; count < 10; count++)
        {
            ReplayHit? hit;
            lock (_stateLock)
            {
                hit = _replayIndex < replay.Hits.Count ? replay.Hits[_replayIndex] : null;
            }
            if (hit == null)
            {
                int exhaustedSequence = game.GetCurrentSequence(controller);
                nint exhaustedFloor = game.GetCurrentFloor(player);
                if (exhaustedSequence < replay.EndTile
                    && exhaustedFloor != 0
                    && game.IsMidSpin(exhaustedFloor)
                    && AdvanceUnrecordedMidSpin(game, controller, player, exhaustedSequence))
                    continue;
                if (replay.Completed)
                    FinishExhaustedReplay(replay, controller);
                return;
            }

            int currentSequence = game.GetCurrentSequence(controller);
            nint floor = game.GetCurrentFloor(player);
            bool midSpin = floor != 0 && game.IsMidSpin(floor);
            if (currentSequence > hit.SequenceId)
            {
                // 编辑器回放若仍落在用户先前选中的砖，不能把记录当作过期输入
                // 一口气丢弃；等待 Play 前置准备/Start_Rewind 把进度同步到回放起点。
                if (editorReplay)
                    return;
                lock (_stateLock)
                    _replayIndex++;
                continue;
            }
            if (currentSequence < hit.SequenceId)
            {
                if (midSpin && AdvanceUnrecordedMidSpin(game, controller, player, currentSequence))
                    continue;
                return;
            }

            nint planet = game.GetChosenPlanet(player);
            if (planet == 0)
                return;
            double targetAngle = 0d;
            if (!midSpin)
            {
                targetAngle = game.GetTargetExitAngle(planet) + hit.HitAngleOffset;
                double currentAngle = game.GetPlanetAngle(planet);
                bool angleReached = game.GetClockwise(player)
                    ? currentAngle >= targetAngle
                    : currentAngle <= targetAngle;
                if (!angleReached)
                    return;
            }

            game.PrepareReplayHit(controller, player);
            if (!midSpin)
                game.SetPlanetAngle(planet, targetAngle);
            bool previousNoFail = game.SetNoFailInfinite(controller, hit.NoFailHit || midSpin);
            int injectedMargin = midSpin || hit.AutoHit || hit.HitMargin is 7 or 10 or 11
                ? 3
                : hit.HitMargin;
            try
            {
                // Final display judgements are not raw timing margins. Keep
                // their recording while injecting a valid movement margin.
                GameHooks.InjectPlayerHit(player, autoHit: true, injectedMargin);
            }
            finally
            {
                game.SetNoFailInfinite(controller, previousNoFail);
            }
            lock (_stateLock)
            {
                if (!ReferenceEquals(_activeReplay, replay))
                    return;
                _replayIndex++;
                if (_runState is ReplayRunState.Finished or ReplayRunState.Failed)
                    return;
            }
        }
    }

    private int GetReplayIndex()
    {
        lock (_stateLock)
            return _replayIndex;
    }

    private static int FindReplayIndexAtOrAfterSequence(ReplayData replay, int sequence)
    {
        if (replay.Hits == null || replay.Hits.Count == 0)
            return 0;
        for (int index = 0; index < replay.Hits.Count; index++)
        {
            if (replay.Hits[index].SequenceId >= sequence)
                return index;
        }
        return replay.Hits.Count;
    }







    private static bool AdvanceUnrecordedMidSpin(
        GameApi game,
        nint controller,
        nint player,
        int currentSequence)
    {
        game.PrepareReplayHit(controller, player);
        bool previousNoFail = game.SetNoFailInfinite(controller, true);
        byte result;
        try
        {
            result = GameHooks.InjectPlayerHit(player, autoHit: true, hitMargin: 3);
        }
        finally
        {
            game.SetNoFailInfinite(controller, previousNoFail);
        }
        return result != 0 && game.GetCurrentSequence(controller) > currentSequence;
    }

    private void BeginReplayInputPlayback()
    {
        lock (_stateLock)
        {
            _replayTouchIndex = 0;
            _replayKeyboardIndex = 0;
            // ReplayTouchInput.TimeMilliseconds uses the same Start_Rewind
            // anchor as recording, so the touch and judgement timelines start
            // from the same game event.
            _replayClockStartTicks = Stopwatch.GetTimestamp();
            _replayPausedTicks = 0;
            _replayPauseStartTicks = 0;
        }
        ReplayKeyViewerApi.BeginPlayback();
    }

    private void EndReplayInputPlayback()
    {
        lock (_stateLock)
        {
            _replayTouchIndex = 0;
            _replayKeyboardIndex = 0;
            _replayClockStartTicks = 0;
            _replayPausedTicks = 0;
            _replayPauseStartTicks = 0;
        }
        ReplayKeyViewerApi.EndPlayback();
    }

    private readonly record struct ReplayInputDispatch(
        ReplayTouchInput? Touch,
        ReplayKeyboardInput? Keyboard);

    private void PublishDueReplayInputEvents(ReplayData replay)
    {
        if (!ReplayKeyViewerApi.IsPlaybackActive)
            return;

        List<ReplayInputDispatch>? due = null;
        lock (_stateLock)
        {
            if (_replayClockStartTicks == 0)
                return;

            long elapsedMilliseconds = GetReplayElapsedMillisecondsLocked();
            replay.TouchEvents ??= new List<ReplayTouchInput>();
            replay.KeyboardEvents ??= new List<ReplayKeyboardInput>();
            while (true)
            {
                ReplayTouchInput? touch = _replayTouchIndex < replay.TouchEvents.Count
                    ? replay.TouchEvents[_replayTouchIndex]
                    : null;
                ReplayKeyboardInput? keyboard = _replayKeyboardIndex < replay.KeyboardEvents.Count
                    ? replay.KeyboardEvents[_replayKeyboardIndex]
                    : null;
                if (touch == null && keyboard == null)
                    break;

                bool takeTouch = touch != null
                    && (keyboard == null || touch.TimeMilliseconds <= keyboard.TimeMilliseconds);
                long eventTime = takeTouch
                    ? touch!.TimeMilliseconds
                    : keyboard!.TimeMilliseconds;
                if (eventTime > elapsedMilliseconds)
                    break;
                due ??= new List<ReplayInputDispatch>();
                if (takeTouch)
                {
                    due.Add(new ReplayInputDispatch(touch, null));
                    _replayTouchIndex++;
                }
                else
                {
                    due.Add(new ReplayInputDispatch(null, keyboard));
                    _replayKeyboardIndex++;
                }
            }
        }

        if (due == null)
            return;
        foreach (ReplayInputDispatch dispatch in due)
        {
            if (dispatch.Touch is { } input)
            {
                ReplayKeyViewerApi.PublishTouch(
                    input.Action,
                    input.PointerId,
                    input.X,
                    input.Y,
                    input.SourceWidth,
                    input.SourceHeight);
            }
            else if (dispatch.Keyboard is { } keyboard)
            {
                ReplayKeyViewerApi.PublishKeyboard(
                    keyboard.Binding,
                    keyboard.Action,
                    keyboard.Repeat);
            }
        }
    }

    private long GetReplayElapsedMillisecondsLocked()
    {
        long now = Stopwatch.GetTimestamp();
        long pausedTicks = _replayPausedTicks;
        if (_replayPauseStartTicks != 0)
            pausedTicks += Math.Max(0L, now - _replayPauseStartTicks);
        long activeTicks = Math.Max(0L, now - _replayClockStartTicks - pausedTicks);
        double milliseconds = activeTicks * 1000d / Stopwatch.Frequency;
        return milliseconds >= long.MaxValue ? long.MaxValue : (long)milliseconds;
    }

    private void PauseReplayClock()
    {
        lock (_stateLock)
        {
            if (_replayClockStartTicks != 0 && _replayPauseStartTicks == 0)
                _replayPauseStartTicks = Stopwatch.GetTimestamp();
        }
    }

    private void ResumeReplayClock()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_stateLock)
        {
            if (_replayPauseStartTicks == 0)
                return;
            _replayPausedTicks += Math.Max(0L, now - _replayPauseStartTicks);
            _replayPauseStartTicks = 0;
        }
    }

    private void SetRecordingAnchor(long anchorTicks)
    {
        lock (_stateLock)
        {
            if (_recordingStartTicks != 0 && _currentAttempt != null)
            {
                _currentAttempt.TouchEvents ??= new List<ReplayTouchInput>();
                _currentAttempt.KeyboardEvents ??= new List<ReplayKeyboardInput>();
                long offsetMilliseconds = GetElapsedMillisecondsBetween(
                    _recordingStartTicks,
                    anchorTicks);
                foreach (ReplayTouchInput input in _currentAttempt.TouchEvents)
                    input.TimeMilliseconds = Math.Max(
                        0L,
                        input.TimeMilliseconds - offsetMilliseconds);
                foreach (ReplayKeyboardInput input in _currentAttempt.KeyboardEvents)
                    input.TimeMilliseconds = Math.Max(
                        0L,
                        input.TimeMilliseconds - offsetMilliseconds);
            }
            _recordingStartTicks = anchorTicks;
        }
    }

    private static long GetElapsedMilliseconds(long startTicks)
    {
        if (startTicks == 0)
            return 0;
        return GetElapsedMillisecondsBetween(startTicks, Stopwatch.GetTimestamp());
    }

    private static long GetElapsedMillisecondsBetween(long startTicks, long endTicks)
    {
        long elapsedTicks = endTicks - startTicks;
        if (elapsedTicks == 0)
            return 0;
        double milliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
        if (milliseconds >= long.MaxValue)
            return long.MaxValue;
        if (milliseconds <= long.MinValue)
            return long.MinValue;
        return (long)milliseconds;
    }

    internal bool HandleFail(nint controller)
    {
        ReplayData? replay;
        lock (_stateLock)
            replay = _activeReplay;
        if (replay != null)
        {
            lock (_stateLock)
                _runState = ReplayRunState.Failed;
            if (_game != null)
                MinimizeReplayDifficultySelector(_game);
            return true;
        }

        float progress = _game?.GetPercentComplete(controller) ?? 0f;
        ReplayData? attempt = FinalizeAttempt(completed: false, controller);
        if (attempt != null)
        {
            bool saved = ShouldAutoSaveAttempt(attempt, progress) && AutoSave(attempt);
            SetResultAttempt(attempt, saved);
        }
        return false;
    }

    internal bool HandleLevelComplete(nint controller)
    {
        ReplayData? replay;
        lock (_stateLock)
            replay = _activeReplay;
        if (replay != null)
        {
            lock (_stateLock)
                _runState = ReplayRunState.Finished;
            if (_game != null)
                MinimizeReplayDifficultySelector(_game);
            return true;
        }

        ReplayData? attempt = FinalizeAttempt(completed: true, controller);
        if (attempt != null)
        {
            bool saved = ShouldAutoSaveAttempt(attempt, 1f) && AutoSave(attempt);
            SetResultAttempt(attempt, saved);
        }
        return false;
    }

    internal bool HandleCustomLevelComplete()
    {
        nint controller;
        lock (_stateLock)
            controller = _controller;
        return HandleLevelComplete(controller);
    }

    internal void ReleaseReplayAfterResult(string result)
    {
        EndReplayInputPlayback();
        if (_replayDifficultySelectorShown && _game != null)
            MinimizeReplayDifficultySelector(_game);
        bool released;
        lock (_stateLock)
        {
            released = _activeReplay != null;
            _activeReplay = null;
            _pendingReplay = null;
            _loadStage = ReplayLoadStage.None;
            _currentAttempt = null;
            _recording = false;
            _replayIndex = 0;
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
            _editorPlayRequested = false;
            _editorFinalized = true;
            _managerPausedGame = false;
            _runState = ReplayRunState.Idle;
        }
        if (released)
            Logger.Info(LogTag, $"Replay state released after {result}");
    }

    private void BeginAttempt(nint controller, int startTile, ReplayLevelIdentity identity, bool forceStart = false)
    {
        GameApi? game = _game;
        if (game == null || (!forceStart && !game.IsGameWorld(controller)))
            return;
        nint player = game.GetPlayer(controller);
        if (player == 0 || _ignoreAutoplay && game.IsPlayerAuto(player))
            return;

        ReplayKeyboardRecorder.Reset();

        ReplayData attempt = new()
        {
            RecordedAtUtc = DateTime.UtcNow,
            SongName = identity.SongName,
            ArtistName = identity.ArtistName,
            LevelPath = identity.LevelPath,
            SceneName = identity.SceneName,
            LevelId = identity.LevelId,
            IsOfficialLevel = identity.IsOfficialLevel,
            Speed = game.GetPitch(),
            Bpm = game.GetBpm(),
            Difficulty = game.GetDifficulty(),
            StartTile = Math.Max(0, startTile),
            EndTile = Math.Max(0, startTile),
            TotalTiles = identity.TotalTiles,
        };

        lock (_stateLock)
        {
            ClearResultAttemptLocked();
            _controller = controller;
            _player = player;
            _currentAttempt = attempt;
            _recording = true;
            if (_recordingStartTicks == 0)
                _recordingStartTicks = Stopwatch.GetTimestamp();
            _runState = ReplayRunState.Idle;
        }
        Logger.Info(
            LogTag,
            $"Recording started: {attempt.SongName}, official={attempt.IsOfficialLevel}, "
            + $"level='{attempt.LevelId}', path='{attempt.LevelPath}', "
            + $"tile {attempt.StartTile}, difficulty={attempt.Difficulty}");
    }

    private ReplayData? FinalizeAttempt(bool completed, nint controller)
    {
        GameApi? game = _game;
        ReplayData? finalized;
        lock (_stateLock)
        {
            if (_currentAttempt == null)
            {
                _recording = false;
                _recordingStartTicks = 0;
                return null;
            }

            _currentAttempt.Completed = completed;
            _currentAttempt.EndTile = Math.Max(
                _currentAttempt.EndTile,
                Math.Max(0, game?.GetCurrentSequence(controller) ?? _currentAttempt.EndTile));

            if (_currentAttempt.Hits.Count == 0)
            {
                Logger.Warn(LogTag, $"Recording ended without captured hits: {_currentAttempt.SongName}");
                _currentAttempt = null;
                _recording = false;
                _recordingStartTicks = 0;
                finalized = null;
            }
            else
            {
                _lastAttempt = CloneReplay(_currentAttempt);
                finalized = CloneReplay(_lastAttempt);
                _currentAttempt = null;
                _recording = false;
                _recordingStartTicks = 0;
                // 编辑器模式下暂停自动录制，等下次 Start_Rewind 触发再恢复。
                if (_editorPlayRequested)
                    _editorFinalized = true;
            }
        }
        ReplayKeyboardRecorder.Reset();

        if (finalized == null)
            return null;
        Logger.Info(
            LogTag,
            $"Recording finalized: {finalized.Hits.Count} hits, "
            + $"touches={finalized.TouchEvents.Count}, keyboard={finalized.KeyboardEvents.Count}, "
            + $"completed={completed}");
        return finalized;
    }

    private void FinishExhaustedReplay(ReplayData replay, nint controller)
    {
        if (!replay.Completed || (_game?.GetCurrentSequence(controller) ?? 0) < replay.EndTile)
            return;
        lock (_stateLock)
            _runState = ReplayRunState.Finished;
        if (_game != null)
            MinimizeReplayDifficultySelector(_game);
    }

    private void QueueSaveCurrent(UiText ui)
    {
        if (!HasAttempt())
        {
            SetNotice(ui.NoAttempt);
            return;
        }
        _commands.Enqueue(new ReplayCommand(ReplayCommandKind.SaveCurrent));
        SetNotice(ui.Queued);
    }

    private void QueuePlayLast(UiText ui)
    {
        ReplayData? replay;
        lock (_stateLock)
            replay = CloneReplay(GetLatestAttemptLocked());
        if (replay == null || replay.Hits.Count == 0)
        {
            SetNotice(ui.NoAttempt);
            return;
        }
        _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Play, replay));
        SetNotice(ui.Queued);
    }

    private bool HasAttempt()
    {
        lock (_stateLock)
            return (_lastAttempt?.Hits.Count ?? 0) > 0 || (_currentAttempt?.Hits.Count ?? 0) > 0;
    }

    private void SaveCurrentNow()
    {
        ReplayData? replay;
        lock (_stateLock)
        {
            ReplayData? latest = GetLatestAttemptLocked();
            replay = CloneReplay(latest);
            if (replay != null && ReferenceEquals(latest, _currentAttempt))
                replay.EndTile = Math.Max(replay.EndTile, _game?.GetCurrentSequence(_controller) ?? replay.EndTile);
        }
        if (replay == null || replay.Hits.Count == 0)
            return;
        SaveReplayNow(replay);
    }

    private bool ShouldAutoSaveAttempt(ReplayData replay, float progress)
    {
        if (_disableTutorialAutoSave && IsTutorialLevel(replay))
            return false;

        return replay.Completed
            ? _saveEveryCompletion || _saveFullClear && replay.StartTile == 0
            : _saveEveryFailure || _saveFailureAt90Percent && progress >= 0.9f;
    }

    private static bool IsTutorialLevel(ReplayData replay)
    {
        if (replay.IsOfficialLevel)
        {
            string levelId = replay.LevelId?.Trim() ?? "";
            int separator = levelId.LastIndexOfAny('/', '\\');
            if (separator >= 0)
                levelId = levelId[(separator + 1)..];
            int extension = levelId.LastIndexOf('.');
            if (extension > 0)
                levelId = levelId[..extension];
            return levelId.Length > 0
                && levelId[^1] >= '0'
                && levelId[^1] <= '9';
        }

        string fileName;
        try
        {
            fileName = Path.GetFileNameWithoutExtension(replay.LevelPath ?? "");
        }
        catch
        {
            return false;
        }

        if (!fileName.StartsWith("sub", StringComparison.OrdinalIgnoreCase))
            return false;
        string suffix = fileName[3..];
        return suffix.All(character => character >= '0' && character <= '9');
    }

    private bool AutoSave(ReplayData replay)
    {
        lock (_stateLock)
        {
            if (!_autoSavedSessions.Add(replay.SessionId))
                return true;
        }
        if (SaveReplayNow(replay))
            return true;
        lock (_stateLock)
            _autoSavedSessions.Remove(replay.SessionId);
        return false;
    }

    private bool SaveReplayNow(ReplayData replay)
    {
        try
        {
            ReplayStore store = RequireStore();
            string path = store.Save(replay, _replayDirectory);
            store.Trim(_replayDirectory, _maximumSavedReplays);
            string message = $"{UiText.FromLanguage(_languageCode).Saved}: {Path.GetFileName(path)}";
            SetNotice(message);
            SetToast(message);
            RefreshFiles();
            Logger.Info(LogTag, $"Saved replay: {path}");
            lock (_stateLock)
            {
                if (_resultAttempt?.SessionId == replay.SessionId)
                {
                    _resultAttemptSaved = true;
                    _resultSaveButtonUntilUtc = DateTime.UtcNow.AddSeconds(5);
                }
                _resultSaveQueued = false;
            }
            return true;
        }
        catch (Exception exception)
        {
            SetNotice(exception.Message);
            Logger.Error(LogTag, $"Save replay failed: {exception}");
            lock (_stateLock)
                _resultSaveQueued = false;
            return false;
        }
    }

    private void SaveResultNow()
    {
        ReplayData? replay;
        lock (_stateLock)
        {
            if (_resultAttemptSaved)
            {
                _resultSaveQueued = false;
                return;
            }
            replay = CloneReplay(_resultAttempt);
        }
        if (replay == null || replay.Hits.Count == 0)
        {
            lock (_stateLock)
                _resultSaveQueued = false;
            return;
        }
        SaveReplayNow(replay);
    }

    /// <summary>
    /// 修正 `1.4.2-mobile.25` 及更早版本录制的回放：那些版本把 Start_Rewind 的参数（常规路径为 -1）
    /// 当成起点，所以从检查点续关的记录会被错误写成 <c>StartTile = 0</c>。
    /// 这里用第一条判定的砖号还原真实起点——回放的第一次判定必然发生在起始砖上，
    /// 否则播放循环会一直等待一个永远不会到达的砖号。
    /// </summary>
    private void RepairLegacyStartTile(ReplayData replay)
    {
        if (replay.Hits.Count == 0 || replay.StartTile > 0)
            return;
        int firstSequence = replay.Hits[0].SequenceId;
        if (firstSequence <= 0)
            return;
        replay.StartTile = firstSequence;
        Logger.Info(
            LogTag,
            $"Repaired legacy replay start tile: {replay.SongName} -> tile {firstSequence}");
    }

    private void StartPlaybackNow(ReplayData replay)
    {
        GameApi? game = _game;
        if (game == null || replay.Hits.Count == 0)
            return;

        StopPlaybackNow(resumeRecording: false);
        _replayDifficultySelectorShown = false;
        ReplayData pendingReplay = CloneReplay(replay)!;
        RepairLegacyStartTile(pendingReplay);
        lock (_stateLock)
        {
            ClearResultAttemptLocked();
            _activeReplay = null;
            _pendingReplay = pendingReplay;
            _customReplayExitRedirectPending = false;
            _currentAttempt = null;
            _runState = ReplayRunState.Loading;
            _loadStage = ReplayLoadStage.WaitingForTargetStart;
            _loadDeadlineUtc = DateTime.UtcNow.Add(CustomLevelBrowserTimeout);
            _replayIndex = 0;
            _recording = false;
            _editorFinalized = false;
        }
        Logger.Info(
            LogTag,
            $"Loading replay: {replay.SongName}, {replay.Hits.Count} hits, "
            + $"official={replay.IsOfficialLevel}, level='{replay.LevelId}', "
            + $"scene='{replay.SceneName}', path='{replay.LevelPath}', difficulty={replay.Difficulty}");

        if (!game.LoadReplayLevel(pendingReplay))
        {
            lock (_stateLock)
            {
                _activeReplay = null;
                _pendingReplay = null;
                _runState = ReplayRunState.Idle;
                _loadStage = ReplayLoadStage.None;
                _loadDeadlineUtc = default;
                _customReplayExitRedirectPending = false;
            }
            string error = string.IsNullOrWhiteSpace(game.LastLoadError)
                ? UiText.FromLanguage(_languageCode).LoadFailed
                : game.LastLoadError;
            SetNotice(error);
            Logger.Error(LogTag, $"Could not load replay level: {replay.SongName}: {error}");
            return;
        }
        lock (_stateLock)
        {
            if (ReferenceEquals(_pendingReplay, pendingReplay))
            {
                _loadStage = game.WaitingForCustomLevelBrowser
                    ? ReplayLoadStage.WaitingForCustomLevelBrowser
                    : game.WaitingForLevelSelect
                        ? ReplayLoadStage.WaitingForLevelSelect
                        : ReplayLoadStage.WaitingForTargetStart;
                _customReplayExitRedirectPending = IsCustomReplayExitCandidate(pendingReplay);
            }
        }
        Logger.Info(LogTag, $"Replay load requested via {game.LastLoadRoute}: {game.GetReplayLoadState()}");
    }

    private void TogglePauseNow()
    {
        GameApi? game = _game;
        if (game == null)
            return;
        nint controller = game.GetController();
        long now = Stopwatch.GetTimestamp();
        lock (_stateLock)
        {
            if (_runState == ReplayRunState.Playing)
            {
                game.SetPaused(controller, true);
                if (_replayClockStartTicks != 0)
                    _replayPauseStartTicks = now;
                _runState = ReplayRunState.Paused;
            }
            else if (_runState == ReplayRunState.Paused)
            {
                game.SetPaused(controller, false);
                if (_replayPauseStartTicks != 0)
                {
                    _replayPausedTicks += Math.Max(0L, now - _replayPauseStartTicks);
                    _replayPauseStartTicks = 0;
                }
                _runState = ReplayRunState.Playing;
            }
        }
    }

    private void StopPlaybackNow(bool resumeRecording = true)
    {
        EndReplayInputPlayback();
        GameApi? game = _game;
        if (game != null)
            MinimizeReplayDifficultySelector(game);
        game?.CancelPendingCustomReplayLoad();
        nint controller = game?.GetController() ?? 0;
        // 编辑器预览页的 gameworld 也是 true，而 set_paused(false) 会直接把预览页
        // 切进游玩状态。只有非编辑器，或编辑器已经处于 Play 模式时才恢复暂停。
        bool editorScene = game?.IsEditorScene() == true;
        if (controller != 0 && game?.IsGameWorld(controller) == true && !editorScene)
            game?.SetPaused(controller, false);
        lock (_stateLock)
        {
            _activeReplay = null;
            _pendingReplay = null;
            _replayIndex = 0;
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
            _editorPlayRequested = false;
            _editorFinalized = false;
            _customReplayExitRedirectPending = false;
            _managerPausedGame = false;
        }
        if (resumeRecording && game?.IsGameWorld(controller) == true && !game.IsEditorScene())
            QueueAttemptStart(controller, game.GetCurrentSequence(controller));
    }

    internal bool HandleCustomReplayQuit(nint controller)
    {
        bool customReplay;
        lock (_stateLock)
        {
            customReplay = _customReplayExitRedirectPending && !_editorPlayRequested;
            if (customReplay)
                _customReplayExitRedirectPending = false;
        }
        if (!customReplay)
            return false;

        StopPlaybackNow(resumeRecording: false);
        _game?.ReleaseCustomLevelState();
        Logger.Info(LogTag, "拦截用户退出自定义谱回放，已清理自定义谱状态并继续调用 QuitToMainMenu。");
        return true;
    }

    private void RefreshFiles()
    {
        try
        {
            List<ReplayFileEntry> files = RequireStore().Scan(_replayDirectory);
            lock (_stateLock)
            {
                _files = files;
                _lastScannedDirectory = _replayDirectory;
            }
        }
        catch (Exception exception)
        {
            SetNotice(exception.Message);
        }
    }

    private ReplayStore RequireStore()
    {
        return _store ?? throw new InvalidOperationException("Replay storage is not initialized");
    }

    private void StartReplayAudioPreviewNow(ReplayChartAudioPreview preview)
    {
        ReplayAudioPreview? audio = _replayAudioPreview;
        GameApi? game = _game;
        if (audio == null || game == null)
            return;
        try
        {
            audio.Start(preview, game.GetConductor());
        }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, "启动回放管理器预览音乐失败: " + exception.Message);
            audio.Stop();
        }
    }

    private void StopReplayAudioPreviewNow()
    {
        try { _replayAudioPreview?.Stop(); }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, "停止回放管理器预览音乐失败: " + exception.Message);
        }
    }

    private void TickReplayAudioPreview()
    {
        ReplayAudioPreview? audio = _replayAudioPreview;
        if (audio == null || !audio.IsActive)
            return;
        try { audio.Tick(); }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, "推进回放管理器预览音乐失败: " + exception.Message);
            audio.Stop();
        }
    }

    private void EnsureManagerPreview(ReplayFileEntry selected)
    {
        string sourcePath = selected.LevelPath?.Trim() ?? "";
        string key = selected.Path + "\n" + sourcePath;
        if (string.Equals(_managerPreviewKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        string chartPath = ReplayChartMetadata.ResolveChartFile(sourcePath) ?? "";
        _managerPreviewKey = key;
        _commands.Enqueue(new ReplayCommand(ReplayCommandKind.StopPreview));

        if (selected.IsOfficialLevel)
        {
            _replayChartPreview?.Clear();
            Logger.Info(
                LogTag,
                "请求显示官谱封面: levelId='" + selected.LevelId
                + "', replay='" + selected.Path + "'");
            _commands.Enqueue(new ReplayCommand(
                ReplayCommandKind.SetOfficialPreview,
                OfficialLevelId: selected.LevelId));
            return;
        }

        _replayChartPreview?.SetChart(chartPath);
        if (string.IsNullOrWhiteSpace(chartPath))
            return;
        ReplayChartAudioPreview? audio = ReplayChartMetadata.ReadAudioPreview(chartPath);
        if (audio != null)
        {
            _commands.Enqueue(new ReplayCommand(
                ReplayCommandKind.StartPreview,
                AudioPreview: audio));
        }
        else
        {
            Logger.Debug(LogTag, "回放对应谱面没有可用的预览音乐: " + chartPath);
        }
    }

    private void StopManagerPreview()
    {
        bool pending = !string.IsNullOrWhiteSpace(_managerPreviewKey);
        _managerPreviewKey = "";
        _replayChartPreview?.Clear();
        if (pending || _replayAudioPreview?.IsActive == true)
            _commands.Enqueue(new ReplayCommand(ReplayCommandKind.StopPreview));
    }

    private void OpenReplayManager()
    {
        bool shouldPause;
        lock (_stateLock)
        {
            shouldPause = !_managerOpen;
            _managerOpen = true;
            if (shouldPause)
                _managerPausedGame = false;
        }
        if (shouldPause)
            _commands.Enqueue(new ReplayCommand(ReplayCommandKind.PauseForManager));
        RefreshFiles();
    }

    private void CloseReplayManager()
    {
        bool resume;
        lock (_stateLock)
        {
            resume = _managerPausedGame;
            _managerOpen = false;
            _managerShowingDetails = false;
            _managerPausedGame = false;
            _pendingDeletePath = "";
            _deletePopupRequested = false;
            _editingReplayPath = "";
            _replayTitleEdit = "";
            _managerTitleEditing = false;
        }
        StopManagerPreview();
        if (resume)
            _commands.Enqueue(new ReplayCommand(ReplayCommandKind.ResumeAfterManager));
    }

    private void PauseForReplayManagerNow()
    {
        GameApi? game = _game;
        nint controller = game?.GetController() ?? 0;
        lock (_stateLock)
        {
            if (!_managerOpen)
                return;
        }
        if (controller == 0
            || game?.IsGameWorld(controller) != true
            || game.IsPaused(controller)
            || game.IsEditorScene())
            return;

        game.SetPaused(controller, true);
        PauseReplayClock();
        lock (_stateLock)
        {
            if (_managerOpen)
            {
                _managerPausedGame = true;
                return;
            }
        }
        game.SetPaused(controller, false);
        ResumeReplayClock();
    }

    private void ResumeAfterReplayManagerNow()
    {
        GameApi? game = _game;
        nint controller = game?.GetController() ?? 0;
        if (controller != 0
            && game?.IsGameWorld(controller) == true
            && !game.IsEditorScene())
        {
            game?.SetPaused(controller, false);
            ResumeReplayClock();
        }
    }

    private void DrawIslandEntry()
    {
        GameApi? game = _game;
        bool editorScene = game?.IsEditorScene() == true;
        if (editorScene)
        {
            // 编辑器内不显示回放管理入口。编辑器的开始/停止由游戏自身
            // 的编辑器按钮处理，避免入口遮挡编辑器并误触发管理页面。
            _islandEntryLogged = false;
            return;
        }
        // Only show the entry on the normal main/level-select page. It must
        // not appear in the custom-level browser or during gameplay.
        bool entryScene = game?.IsLevelSelect() == true;
        if (!entryScene)
            _islandEntryLogged = false;
        ImGuiIOPtr io = ImGui.GetIO();
        // On 3.1.2 the manager settings callback can remain active while the
        // main manager window is hidden. The entry has its own window id and
        // is already hidden when ReplayManager is open, so it must not depend
        // on either the settings callback timestamp or a stale WantTextInput
        // flag from the mobile keyboard.
        if (!entryScene || game == null || _managerOpen)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        Vector2 display = io.DisplaySize;
        if (display.X < 200f || display.Y < 120f)
            return;
        ImGuiStylePtr style = ImGui.GetStyle();
        float margin = GetOverlayMargin();
        float buttonHeight = Math.Max(46f, ImGui.GetFrameHeight() * 1.25f);
        string entryLabel = ui.IslandEntry;
        float desiredWidth = ImGui.CalcTextSize(entryLabel).X
            + style.FramePadding.X * 2f
            + style.WindowPadding.X * 2f;
        float width = ClampOverlayWidth(display.X, margin, 220f, desiredWidth);
        Vector2 size = new(width, buttonHeight + style.WindowPadding.Y * 2f);
        float posX = display.X - size.X - margin;
        float posY = display.Y - size.Y - margin;
        ImGui.SetNextWindowPos(new Vector2(posX, posY), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        Vector4 accent = new(0.98f, 0.43f, 0.52f, 1f);
        int colorCount = 0;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.10f, 0.075f, 0.11f, 0.96f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.13f, 0.18f, 1f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accent);
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.84f, 0.27f, 0.37f, 1f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.92f, 0.95f, 1f));
        colorCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayMainEntry", flags))
        {
            if (ImGui.Button(entryLabel, new Vector2(-1f, buttonHeight)))
                OpenReplayManager();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(colorCount);
        if (!_islandEntryLogged)
        {
            _islandEntryLogged = true;
            Logger.Info(LogTag, "Replay main-page entry is available");
        }
    }

    private void DrawReplayControls()
    {
        if (_managerOpen)
            return;
        ReplayData? replay;
        ReplayRunState state;
        lock (_stateLock)
        {
            replay = _activeReplay;
            state = _runState;
        }
        if (replay == null || state == ReplayRunState.Loading)
            return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X < 260f || display.Y < 120f)
            return;
        UiText ui = UiText.FromLanguage(_languageCode);
        ImGuiStylePtr style = ImGui.GetStyle();
        float margin = GetOverlayMargin();
        float buttonHeight = Math.Max(48f, ImGui.GetFrameHeight() * 1.25f);
        float headerHeight = Math.Max(44f, ImGui.GetFrameHeight() * 1.22f);
        string primaryLabel = state == ReplayRunState.Paused ? ui.Resume : ui.Pause;
        string stateLabel = state switch
        {
            ReplayRunState.Paused => ui.ReplayPaused,
            ReplayRunState.Finished => ui.ReplayFinished,
            ReplayRunState.Failed => ui.ReplayFailed,
            ReplayRunState.WaitingForStart => ui.ReplayWaiting,
            _ => ui.Replaying,
        };
        float buttonWidth = Math.Max(
            110f,
            Math.Max(ImGui.CalcTextSize(primaryLabel).X, ImGui.CalcTextSize(ui.Stop).X)
                + style.FramePadding.X * 4f);
        float bodyContentHeight = buttonHeight
            + style.WindowPadding.Y * 2f
            + style.ItemSpacing.Y;
        float headerWidth = ImGui.CalcTextSize(stateLabel).X
            + ImGui.CalcTextSize("REPLAY").X
            + 76f;
        float desiredWidth = state is ReplayRunState.Playing or ReplayRunState.Paused
            ? Math.Max(headerWidth, buttonWidth * 2f + style.ItemSpacing.X)
            : Math.Max(headerWidth, buttonWidth);
        float width = ClampOverlayWidth(display.X, margin, 280f, desiredWidth);
        float sizeHeight = headerHeight
            + (_replayControlsExpanded ? bodyContentHeight : 0f)
            + style.WindowPadding.Y * 2f;
        Vector2 size = new(width, sizeHeight + style.WindowPadding.Y);
        ImGui.SetNextWindowPos(new Vector2(margin, display.Y - size.Y - margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        PushReplayOverlayTheme(out int colorCount, out int styleCount);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayControls", flags))
        {
            if (ImGui.BeginChild(
                    "##ReplayControlsHeader",
                    new Vector2(0f, headerHeight),
                    ImGuiChildFlags.None,
                    ImGuiWindowFlags.NoScrollbar))
            {
                ImGui.TextColored(new Vector4(0.98f, 0.43f, 0.52f, 1f), "REPLAY");
                ImGui.SameLine();
                ImGui.TextDisabled(stateLabel);
                float toggleWidth = 38f;
                ImGui.SameLine();
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - toggleWidth - 4f));
                bool toggleClicked = ImGui.Button(
                    "##ReplayControlsToggle",
                    new Vector2(toggleWidth, headerHeight - 8f));
                Vector2 toggleMin = ImGui.GetItemRectMin();
                Vector2 toggleMax = ImGui.GetItemRectMax();
                DrawReplayControlsChevron(
                    ImGui.GetWindowDrawList(),
                    (toggleMin + toggleMax) * 0.5f,
                    _replayControlsExpanded,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.80f, 0.86f, 1f)));
                if (toggleClicked)
                    _replayControlsExpanded = !_replayControlsExpanded;
            }
            ImGui.EndChild();

            if (_replayControlsExpanded)
            {
                if (ImGui.BeginChild(
                        "##ReplayControlsBody",
                        new Vector2(0f, bodyContentHeight),
                        ImGuiChildFlags.AlwaysUseWindowPadding,
                        ImGuiWindowFlags.NoScrollbar))
                {
                    ImGui.Separator();
                    if (state is ReplayRunState.Playing or ReplayRunState.Paused)
                    {
                        float rowWidth = ImGui.GetContentRegionAvail().X;
                        float actionWidth = Math.Max(
                            1f,
                            (rowWidth - style.ItemSpacing.X) * 0.5f);
                        if (ImGui.Button(primaryLabel, new Vector2(actionWidth, buttonHeight)))
                            _commands.Enqueue(new ReplayCommand(ReplayCommandKind.TogglePause));
                        ImGui.SameLine();
                        if (ImGui.Button(ui.Stop, new Vector2(actionWidth, buttonHeight)))
                            _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
                    }
                    else if (ImGui.Button(ui.Stop, new Vector2(-1f, buttonHeight)))
                    {
                        _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
                    }
                }
                ImGui.EndChild();
            }
        }
        ImGui.End();
        PopReplayOverlayTheme(colorCount, styleCount);
    }
    private static void DrawReplayControlsChevron(
        ImDrawListPtr drawList,
        Vector2 center,
        bool expanded,
        uint color)
    {
        float size = 6f;
        if (expanded)
        {
            drawList.AddLine(center + new Vector2(-size, -size * 0.35f),
                center + new Vector2(0f, size * 0.45f), color, 2f);
            drawList.AddLine(center + new Vector2(0f, size * 0.45f),
                center + new Vector2(size, -size * 0.35f), color, 2f);
        }
        else
        {
            drawList.AddLine(center + new Vector2(-size * 0.35f, -size),
                center + new Vector2(size * 0.45f, 0f), color, 2f);
            drawList.AddLine(center + new Vector2(size * 0.45f, 0f),
                center + new Vector2(-size * 0.35f, size), color, 2f);
        }
    }

    private void DrawResultSaveButton()
    {
        if (_managerOpen)
            return;
        long settingsAge = Environment.TickCount64 - Interlocked.Read(ref _lastSettingsGuiTick);
        if (settingsAge < 250)
            return;

        bool visible;
        bool saved;
        bool queued;
        lock (_stateLock)
        {
            visible = _resultAttempt is { Hits.Count: > 0 }
                && _activeReplay == null
                && _pendingReplay == null
                && !_recording;
            saved = _resultAttemptSaved;
            queued = _resultSaveQueued;
            if (saved
                && _resultSaveButtonUntilUtc != default
                && DateTime.UtcNow >= _resultSaveButtonUntilUtc)
            {
                visible = false;
            }
        }
        if (!visible)
            return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X < 220f || display.Y < 120f)
            return;
        UiText ui = UiText.FromLanguage(_languageCode);
        ImGuiStylePtr style = ImGui.GetStyle();
        float margin = GetOverlayMargin();
        float buttonHeight = GetOverlayButtonHeight();
        string label = SanitizeImGuiText(saved ? ui.ResultSaved : queued ? ui.SavingResult : ui.SaveResult);
        string statusLabel = SanitizeImGuiText(saved ? ui.ResultSaved : ui.SaveResult);
        float headerWidth = ImGui.CalcTextSize("RESULT").X
            + style.ItemSpacing.X
            + ImGui.CalcTextSize(statusLabel).X;
        float actionWidth = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f;
        float desiredWidth = Math.Max(headerWidth, actionWidth)
            + style.WindowPadding.X * 2f;
        float width = ClampOverlayWidth(
            display.X,
            margin,
            Math.Min(220f, desiredWidth),
            desiredWidth);
        float contentHeight = ImGui.GetTextLineHeightWithSpacing()
            + style.ItemSpacing.Y
            + 1f
            + buttonHeight;
        Vector2 size = new(width, contentHeight + style.WindowPadding.Y * 2f);
        ImGui.SetNextWindowPos(new Vector2(margin, display.Y - size.Y - margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        PushReplayOverlayTheme(out int colorCount, out int styleCount);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayResultSave", flags))
        {
            ImGui.TextColored(new Vector4(0.98f, 0.43f, 0.52f, 1f), "RESULT");
            ImGui.SameLine();
            ImGui.TextDisabled(saved ? ui.ResultSaved : ui.SaveResult);
            ImGui.Separator();
            float buttonWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
            if (ImGui.Button(label, new Vector2(buttonWidth, buttonHeight)) && !saved && !queued)
            {
                lock (_stateLock)
                    _resultSaveQueued = true;
                SaveResultNow();
            }
        }
        ImGui.End();
        PopReplayOverlayTheme(colorCount, styleCount);
    }

    private void DrawToast()
    {
        string toast;
        DateTime until;
        lock (_stateLock)
        {
            toast = _toast;
            until = _toastUntilUtc;
            if (!string.IsNullOrEmpty(toast) && DateTime.UtcNow >= until)
            {
                _toast = "";
                _toastUntilUtc = default;
                return;
            }
        }
        if (string.IsNullOrEmpty(toast))
            return;

        toast = SanitizeImGuiText(toast);
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X < 180f || display.Y < 100f)
            return;
        ImGuiStylePtr style = ImGui.GetStyle();
        float margin = GetOverlayMargin();
        float desiredWidth = ImGui.CalcTextSize(toast).X + style.WindowPadding.X * 2f;
        float width = ClampOverlayWidth(display.X, margin, 420f, desiredWidth);
        float wrapWidth = Math.Max(1f, width - style.WindowPadding.X * 2f);
        float textHeight = ImGui.CalcTextSize(toast, false, wrapWidth).Y;
        Vector2 size = new(width, Math.Max(64f, textHeight + style.WindowPadding.Y * 2f));
        ImGui.SetNextWindowPos(new Vector2((display.X - size.X) * 0.5f, margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        PushReplayOverlayTheme(out int colorCount, out int styleCount);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##ReplayToast", flags))
        {
            DrawNoticeIcon(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos());
            ImGui.SameLine(0f, 10f);
            ImGui.TextWrapped(toast);
        }
        ImGui.End();
        PopReplayOverlayTheme(colorCount, styleCount);
    }

    private static float GetOverlayMargin()
    {
        return Math.Max(18f, ImGui.GetFontSize() * 0.65f);
    }

    private static float GetOverlayButtonHeight()
    {
        return Math.Max(38f, ImGui.GetFrameHeight() * 1.2f);
    }

    private static float GetOverlayWindowHeight(float buttonHeight)
    {
        return buttonHeight + ImGui.GetStyle().WindowPadding.Y * 2f;
    }

    private static float ClampOverlayWidth(float displayWidth, float margin, float minimum, float desired)
    {
        float available = Math.Max(1f, displayWidth - margin * 2f);
        return Math.Min(available, Math.Max(Math.Min(minimum, available), desired));
    }

    private static void PushReplayOverlayTheme(out int colorCount, out int styleCount)
    {
        Vector4 accent = new(0.98f, 0.43f, 0.52f, 1f);
        colorCount = 0;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.10f, 0.075f, 0.11f, 0.96f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.42f, 0.28f, 0.36f, 1f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.13f, 0.18f, 1f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accent);
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.84f, 0.27f, 0.37f, 1f));
        colorCount++;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.92f, 0.95f, 1f));
        colorCount++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        styleCount = 2;
    }

    private static void PopReplayOverlayTheme(int colorCount, int styleCount)
    {
        ImGui.PopStyleVar(styleCount);
        ImGui.PopStyleColor(colorCount);
    }

    private void DrawReplayManager()
    {
        if (!_managerOpen)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0f || display.Y <= 0f)
            return;

        Vector4 ink = new(0.92f, 0.96f, 0.95f, 1f);
        Vector4 muted = new(0.58f, 0.68f, 0.67f, 1f);
        Vector4 accent = new(0.98f, 0.43f, 0.52f, 1f);
        Vector4 surface = new(0.10f, 0.075f, 0.11f, 0.99f);
        Vector4 surfaceRaised = new(0.18f, 0.13f, 0.19f, 1f);
        Vector4 surfaceHover = new(0.30f, 0.18f, 0.25f, 1f);
        int colors = 0;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, surface); colors++;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, surface); colors++;
        ImGui.PushStyleColor(ImGuiCol.Text, ink); colors++;
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, muted); colors++;
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.16f, 0.28f, 0.28f, 1f)); colors++;
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.16f, 0.28f, 0.28f, 1f)); colors++;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, surfaceRaised); colors++;
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, surfaceHover); colors++;
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.17f, 0.32f, 0.31f, 1f)); colors++;
        ImGui.PushStyleColor(ImGuiCol.Button, surfaceRaised); colors++;
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, surfaceHover); colors++;
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.12f, 0.62f, 0.56f, 1f)); colors++;
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.10f, 0.25f, 0.24f, 1f)); colors++;
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, surfaceHover); colors++;
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.12f, 0.62f, 0.56f, 1f)); colors++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display, ImGuiCond.Always);
        bool open = true;
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin("###ReplayManager", ref open, flags))
        {
            // Compact app bar: title, current version, and one clear close action.
            ImGui.TextColored(accent, "REPLAY");
            ImGui.SameLine(0f, 10f);
            ImGui.TextUnformatted(ui.ManagerTitle);
            ImGui.SameLine(0f, 10f);
            ImGui.TextDisabled($"v{Version}");
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 78f);
            if (DrawIconButton("close", "##manager-close", new Vector2(72f, 48f)))
                open = false;
            ImGui.Separator();

            // Horizontal segmented navigation keeps the content wide and avoids a permanent sidebar.
            float tabWidth = Math.Max(1f, (ImGui.GetContentRegionAvail().X - 24f) / 3f);
            if (DrawReplayTopTab(ui.ManagerTitle, 0, _managerSection == 0, tabWidth)) _managerSection = 0;
            ImGui.SameLine();
            if (DrawReplayTopTab(GetManagerSettingsLabel(), 1, _managerSection == 1, tabWidth)) _managerSection = 1;
            ImGui.SameLine();
            if (DrawReplayTopTab(GetManagerUpdatesLabel(), 2, _managerSection == 2, tabWidth)) _managerSection = 2;
            ImGui.Spacing();

            if (ImGui.BeginChild("##ReplayManagerContent", Vector2.Zero,
                    ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding,
                    ImGuiWindowFlags.AlwaysVerticalScrollbar))
            {
                if (_managerSection == 1)
                    DrawReplayManagerSettings(ui);
                else if (_managerSection == 2)
                    DrawReplayManagerUpdates();
                else
                {
                    List<ReplayFileEntry> files;
                    lock (_stateLock) files = _files.ToList();
                    ReplayFileEntry? selected = files.FirstOrDefault(entry =>
                        string.Equals(entry.Path, _selectedReplayPath, StringComparison.Ordinal));
                    if (_managerShowingDetails && selected != null)
                        DrawReplayDetailsPage(ui, selected, ref open);
                    else
                    {
                        _managerShowingDetails = false;
                        StopManagerPreview();
                        DrawReplayListPage(ui, files, ref open);
                    }
                }
                ImGui.EndChild();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(6);
        ImGui.PopStyleColor(colors);
        if (!open)
            CloseReplayManager();
    }


    private static bool DrawReplayTopTab(string label, int id, bool active, float width)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##replay-tab-{id}", new Vector2(width, 42f));
        bool hovered = ImGui.IsItemHovered();
        Vector4 fill = active
            ? new Vector4(0.38f, 0.17f, 0.27f, 1f)
            : hovered ? new Vector4(0.24f, 0.14f, 0.21f, 1f) : new Vector4(0.16f, 0.11f, 0.16f, 1f);
        uint color = ImGui.ColorConvertFloat4ToU32(fill);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(width, 42f), color, 7f);
        if (active)
            draw.AddRectFilled(new Vector2(min.X, min.Y + 38f), new Vector2(min.X + width, min.Y + 42f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.98f, 0.43f, 0.52f, 1f)), 2f);
        Vector2 text = min + new Vector2((width - ImGui.CalcTextSize(label).X) * 0.5f,
            (42f - ImGui.GetTextLineHeight()) * 0.5f);
        draw.AddText(text, ImGui.ColorConvertFloat4ToU32(active
            ? new Vector4(1f, 0.92f, 0.95f, 1f)
            : new Vector4(0.72f, 0.66f, 0.72f, 1f)), label);
        return clicked;
    }

    private static bool DrawManagerNavigationButton(
        string icon,
        string label,
        bool active,
        string id)
    {
        float height = Math.Max(44f, ImGui.GetFrameHeight() * 1.25f);
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        Vector2 min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##manager-nav-{id}", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector4 baseColor = active
            ? new Vector4(0.98f, 0.43f, 0.52f, 1f)
            : hovered
                ? new Vector4(0.32f, 0.19f, 0.27f, 1f)
                : new Vector4(0.14f, 0.10f, 0.15f, 0.92f);
        uint background = ImGui.ColorConvertFloat4ToU32(baseColor);
        drawList.AddRectFilled(min, min + new Vector2(width, height), background, 6f);
        float iconSize = Math.Min(26f, height - 12f);
        Vector2 center = min + new Vector2(16f + iconSize * 0.5f, height * 0.5f);
        DrawManagerNavigationIcon(
            drawList,
            icon,
            center,
            iconSize * 0.5f,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.75f, 0.82f, 1f)));
        float availableTextWidth = Math.Max(20f, width - iconSize - 42f);
        string displayLabel = EllipsizeManagerText(
            SanitizeImGuiText(label),
            availableTextWidth);
        drawList.AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize(),
            new Vector2(min.X + iconSize + 28f, min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.94f, 0.97f, 1f)),
            displayLabel);
        return clicked;
    }

    private static void DrawManagerNavigationIcon(
        ImDrawListPtr drawList,
        string icon,
        Vector2 center,
        float radius,
        uint color)
    {
        switch (icon)
        {
            case "replays":
                Vector2 cardMin = center - new Vector2(radius * 0.72f, radius * 0.58f);
                Vector2 cardMax = center + new Vector2(radius * 0.72f, radius * 0.58f);
                drawList.AddRect(cardMin, cardMax, color, 3f, ImDrawFlags.None, 2f);
                drawList.AddLine(
                    new Vector2(cardMin.X + radius * 0.2f, cardMin.Y),
                    new Vector2(cardMin.X + radius * 0.2f, cardMax.Y),
                    color,
                    1.5f);
                drawList.AddTriangleFilled(
                    center + new Vector2(-radius * 0.18f, -radius * 0.30f),
                    center + new Vector2(radius * 0.36f, 0f),
                    center + new Vector2(-radius * 0.18f, radius * 0.30f),
                    color);
                break;
            case "settings":
                drawList.AddCircle(center, radius * 0.48f, color, 16, 2f);
                drawList.AddCircleFilled(center, radius * 0.16f, color, 12);
                for (int index = 0; index < 8; index++)
                {
                    float angle = index * MathF.PI / 4f;
                    Vector2 direction = new(MathF.Cos(angle), MathF.Sin(angle));
                    drawList.AddLine(
                        center + direction * radius * 0.62f,
                        center + direction * radius * 0.92f,
                        color,
                        2.5f);
                }
                break;
            default:
                drawList.AddCircle(center, radius * 0.65f, color, 24, 2f);
                Vector2 arrowTip = center + new Vector2(radius * 0.70f, -radius * 0.48f);
                drawList.AddTriangleFilled(
                    arrowTip,
                    arrowTip + new Vector2(-radius * 0.12f, radius * 0.45f),
                    arrowTip + new Vector2(-radius * 0.48f, radius * 0.10f),
                    color);
                break;
        }
    }

    private string GetDifficultyLabel(int difficulty)
    {
        int normalized = Math.Clamp(difficulty, 0, 2);
        return _languageCode switch
        {
            6 or 40 or 41 => normalized switch { 0 => "宽松", 2 => "严格", _ => "普通" },
            22 => normalized switch { 0 => "ゆるい", 2 => "厳しい", _ => "普通" },
            23 => normalized switch { 0 => "관대", 2 => "엄격", _ => "일반" },
            _ => normalized switch { 0 => "Lenient", 2 => "Strict", _ => "Normal" },
        };
    }

    private string GetManagerSettingsLabel()
    {
        return _languageCode switch
        {
            6 or 40 or 41 => "设置",
            22 => "設定",
            23 => "설정",
            _ => "Settings",
        };
    }

    private string GetManagerUpdatesLabel()
    {
        return _languageCode switch
        {
            6 or 40 or 41 => "更新",
            22 => "更新",
            23 => "업데이트",
            _ => "Updates",
        };
    }

    private string GetCheckUpdatesLabel()
    {
        return _languageCode switch
        {
            6 or 40 or 41 => "检查更新",
            22 => "更新を確認",
            23 => "업데이트 확인",
            _ => "Check for updates",
        };
    }

    private string GetInstallUpdateLabel()
    {
        return _languageCode switch
        {
            6 or 40 or 41 => "下载并安装更新",
            22 => "更新をダウンロードしてインストール",
            23 => "업데이트 다운로드 및 설치",
            _ => "Download and install update",
        };
    }

    private void DrawReplayManagerSettings(UiText ui)
    {
        DrawManagerSectionTitle(GetManagerSettingsLabel());
        ImGui.TextDisabled("Replay Mobile");
        bool changed = false;
        ImGui.BeginChild("##settings-recording", new Vector2(-1f, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        DrawManagerSectionTitle(ui.SaveOptions);
        changed |= ImGui.Checkbox(ui.SaveFullClear, ref _saveFullClear);
        changed |= ImGui.Checkbox(ui.SaveEveryCompletion, ref _saveEveryCompletion);
        changed |= ImGui.Checkbox(ui.SaveEveryFailure, ref _saveEveryFailure);
        changed |= ImGui.Checkbox(ui.SaveFailureAt90Percent, ref _saveFailureAt90Percent);
        changed |= ImGui.Checkbox(ui.DisableTutorialAutoSave, ref _disableTutorialAutoSave);
        ImGui.EndChild();
        ImGui.Spacing();
        ImGui.BeginChild("##settings-input", new Vector2(-1f, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        DrawManagerSectionTitle("回放与输入");
        changed |= ImGui.Checkbox(ui.DisableAutoReplay, ref _ignoreAutoplay);
        changed |= ImGui.Checkbox(ui.ShowHud, ref _showReplayHud);
        changed |= ImGui.Checkbox(ui.ReceiveTouchInput, ref _receiveTouchInput);
        changed |= ImGui.Checkbox(ui.ReceiveKeyboardInput, ref _receiveKeyboardInput);
        if (_showReplayHud)
        {
            changed |= ImGui.SliderInt(ui.HudSize, ref _hudFontSize, 12, 64);
            changed |= ImGui.SliderFloat("HUD X", ref _hudPositionX, 0f, 1f, "%.2f");
            changed |= ImGui.SliderFloat("HUD Y", ref _hudPositionY, 0f, 1f, "%.2f");
        }
        ImGui.EndChild();
        ImGui.Spacing();
        ImGui.BeginChild("##settings-storage", new Vector2(-1f, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        DrawManagerSectionTitle(ui.Directory);
        changed |= ImGui.SliderInt(ui.MaxFiles, ref _maximumSavedReplays, 1, 500);
        changed |= ImGui.InputText(ui.Directory, ref _replayDirectory, 512);
        ImGui.EndChild();
        if (changed)
        {
            NormalizeSettings();
            SyncInputReceivers();
            SaveSettings();
        }
    }

    private void DrawReplayManagerUpdates()
    {
        string updatesLabel = GetManagerUpdatesLabel();
        DrawManagerSectionTitle(updatesLabel);
        ImGui.TextDisabled($"Replay Mobile {Version}");
        ImGui.Separator();

        GitHubUpdateService? updater = _updateService;
        if (updater == null)
        {
            ImGui.TextDisabled("更新服务不可用。");
            return;
        }

        ReplayUpdateSnapshot snapshot = updater.GetManagerSnapshot();
        if (snapshot.Checking || snapshot.Downloading)
        {
            ImGui.TextDisabled(SanitizeImGuiText(snapshot.Status));
            return;
        }

        if (snapshot.HasUpdate)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.78f, 0.25f, 1f),
                $"发现新版本：{snapshot.Version}");
            ImGui.TextDisabled(SanitizeImGuiText(snapshot.Status));
            if (ImGui.Button(GetInstallUpdateLabel(), new Vector2(-1f, GetManagerButtonHeight())))
                updater.DownloadUpdate();
            ImGui.Spacing();
            DrawManagerSectionTitle($"v{snapshot.Version} 更新日志");
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(snapshot.Notes)
                ? "该版本未提供更新日志。"
                : SanitizeImGuiText(snapshot.Notes));
        }
        else
        {
            if (snapshot.ReadyToRestart)
            {
                ImGui.TextColored(
                    new Vector4(0.45f, 1f, 0.65f, 1f),
                    SanitizeImGuiText(snapshot.Status));
            }
            else if (snapshot.Failed)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.4f, 0.4f, 1f),
                    SanitizeImGuiText(snapshot.Status));
            }
            else
            {
                ImGui.TextColored(
                    new Vector4(0.45f, 1f, 0.65f, 1f),
                    $"当前已是最新版本：{Version}");
            }

            ImGui.Spacing();
            DrawManagerSectionTitle($"v{Version} 更新日志");
            if (string.IsNullOrWhiteSpace(snapshot.Notes))
                ImGui.TextDisabled("GitHub 未提供该版本的更新日志。");
            else
                ImGui.TextWrapped(SanitizeImGuiText(snapshot.Notes));
        }

        ImGui.Spacing();
        if (ImGui.Button(GetCheckUpdatesLabel(), new Vector2(-1f, GetManagerButtonHeight())))
            updater.CheckNow();
    }

    private void DrawReplayListPage(UiText ui, List<ReplayFileEntry> files, ref bool open)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float buttonHeight = GetManagerButtonHeight();
        ImGui.TextColored(new Vector4(0.24f, 0.88f, 0.78f, 1f), ui.ManagerTitle);
        ImGui.SameLine();
        ImGui.TextDisabled($"{files.Count}  {ui.Files}");
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - 180f));
        if (DrawIconButton("refresh", "##replay-refresh", new Vector2(78f, buttonHeight)))
            RefreshFiles();
        ImGui.SameLine();
        if (DrawIconButton("close", "##replay-close", new Vector2(78f, buttonHeight)))
            open = false;

        ShowNotice();
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##replay-search", ui.Search, ref _fileSearch, 128);

        string search = _fileSearch.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            files = files.Where(entry =>
                    entry.DisplayTitle.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || entry.SongName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || entry.ArtistName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        DrawManagerSectionTitle($"{ui.Files}: {files.Count}");
        ImGui.Separator();
        if (!ImGui.BeginChild(
                "##ReplayFileList",
                Vector2.Zero,
                ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        if (files.Count == 0)
        {
            ImGui.TextDisabled(ui.NoFiles);
        }
        else
        {
            float rowHeight = Math.Max(
                92f,
                ImGui.GetTextLineHeightWithSpacing() * 3f + style.FramePadding.Y * 2f);
            foreach (ReplayFileEntry entry in files)
            {
                string result = entry.Supported
                    ? entry.Completed ? ui.Complete : ui.Failed
                    : ui.Unsupported;
                int progress = GetEndProgress(entry);
                float rowWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
                bool selected = string.Equals(_selectedReplayPath, entry.Path, StringComparison.Ordinal);
                Vector2 rowMin = ImGui.GetCursorScreenPos();
                Vector2 rowMax = rowMin + new Vector2(rowWidth, rowHeight);
                ImDrawListPtr drawList = ImGui.GetWindowDrawList();
                uint rowColor = ImGui.ColorConvertFloat4ToU32(selected
                    ? new Vector4(0.24f, 0.12f, 0.20f, 1f)
                    : new Vector4(0.12f, 0.085f, 0.13f, 1f));
                uint rowBorder = ImGui.ColorConvertFloat4ToU32(selected
                    ? new Vector4(0.98f, 0.43f, 0.52f, 0.9f)
                    : new Vector4(0.26f, 0.18f, 0.27f, 1f));
                drawList.AddRectFilled(rowMin, rowMax, rowColor, 7f);
                drawList.AddRect(rowMin, rowMax, rowBorder, 7f, 0, selected ? 1.5f : 1f);

                ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(1f, 1f, 1f, 0.03f));
                ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(1f, 1f, 1f, 0.05f));
                bool clicked = ImGui.Selectable(
                    $"##replay-row-{entry.Path}",
                    selected,
                    ImGuiSelectableFlags.None,
                    new Vector2(rowWidth, rowHeight));
                ImGui.PopStyleColor(3);

                string title = EllipsizeManagerText(
                    GetReplayDisplayTitle(entry),
                    Math.Max(100f, rowWidth - 180f));
                string metadata = $"{entry.RecordedAtUtc.ToLocalTime():MM-dd HH:mm}  {entry.HitCount} {ui.Inputs}";
                Vector4 statusVector = !entry.Supported
                    ? new Vector4(1f, 0.45f, 0.42f, 1f)
                    : entry.Completed
                        ? new Vector4(0.42f, 1f, 0.65f, 1f)
                        : new Vector4(1f, 0.78f, 0.32f, 1f);
                uint statusColor = ImGui.ColorConvertFloat4ToU32(statusVector);
                Vector2 textStart = rowMin + new Vector2(16f, 11f);
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.05f, textStart, 0xFFFFFFFF, title);
                drawList.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize() * 0.86f,
                    textStart + new Vector2(0f, 27f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.68f, 0.60f, 0.66f, 1f)),
                    SanitizeImGuiText(metadata));
                drawList.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize() * 0.86f,
                    new Vector2(rowMax.X - 122f, textStart.Y),
                    statusColor,
                    SanitizeImGuiText(result));
                drawList.AddText(
                    ImGui.GetFont(),
                    ImGui.GetFontSize() * 0.86f,
                    new Vector2(rowMax.X - 122f, textStart.Y + 27f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.82f, 0.76f, 0.80f, 1f)),
                    $"{progress}%");
                float progressWidth = Math.Max(1f, rowWidth - 32f);
                Vector2 progressMin = new(rowMin.X + 16f, rowMax.Y - 10f);
                Vector2 progressMax = new(rowMin.X + 16f + progressWidth * progress / 100f, rowMax.Y - 6f);
                drawList.AddRectFilled(
                    new Vector2(rowMin.X + 16f, rowMax.Y - 10f),
                    new Vector2(rowMax.X - 16f, rowMax.Y - 6f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.24f, 0.18f, 0.24f, 1f)),
                    2f);
                if (progress > 0)
                    drawList.AddRectFilled(progressMin, progressMax, statusColor, 2f);

                if (!clicked)
                    continue;

                _selectedReplayPath = entry.Path;
                _pendingDeletePath = "";
                _deletePopupRequested = false;
                _editingReplayPath = entry.Path;
                _replayTitleEdit = entry.Title;
                _managerTitleEditing = false;
                _managerShowingDetails = true;
            }
        }
        ImGui.EndChild();
    }

    private void DrawReplayDetailsPage(UiText ui, ReplayFileEntry selected, ref bool open)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float buttonHeight = GetManagerButtonHeight();
        float buttonWidth = Math.Max(
            1f,
            (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) * 0.5f);
        if (DrawIconButton("back", "##details-back", new Vector2(buttonWidth, buttonHeight)))
        {
            _managerShowingDetails = false;
            _pendingDeletePath = "";
            _deletePopupRequested = false;
            _managerTitleEditing = false;
            StopManagerPreview();
        }
        ImGui.SameLine();
        if (DrawIconButton("close", "##details-close", new Vector2(buttonWidth, buttonHeight)))
            open = false;

        ShowNotice();
        ImGui.Separator();
        if (ImGui.BeginChild(
                "##ReplayDetailsPage",
                Vector2.Zero,
                ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
            DrawReplayDetails(ui, selected);
        ImGui.EndChild();
    }

    private void DrawReplayDetails(UiText ui, ReplayFileEntry selected)
    {
        if (!selected.Supported)
        {
            DrawReplayDetailsInfo(ui, selected);
        }
        else
        {
            Vector2 available = ImGui.GetContentRegionAvail();
            if (available.X < 860f)
            {
                DrawReplayDetailsInfo(ui, selected);
                ImGui.Spacing();
                DrawReplayChartPreview(ui, selected);
                ImGui.Spacing();
                DrawReplayProgressCard(ui, selected);
            }
            else
            {
                ImGuiStylePtr style = ImGui.GetStyle();
                float spacing = style.ItemSpacing.X;
                float coverWidth = Math.Min(440f, Math.Max(300f, available.X * 0.34f));
                float infoWidth = Math.Max(1f, available.X - coverWidth - spacing);
                Vector2 rowStart = ImGui.GetCursorScreenPos();

                ImGui.BeginGroup();
                DrawReplayDetailsInfo(ui, selected, infoWidth);
                Vector2 infoBottom = ImGui.GetItemRectMax();
                ImGui.EndGroup();

                ImGui.SetCursorScreenPos(new Vector2(rowStart.X + infoWidth + spacing, rowStart.Y));
                ImGui.BeginGroup();
                DrawReplayChartPreview(ui, selected, 320f, Math.Max(0f, infoBottom.Y - rowStart.Y));
                ImGui.EndGroup();
                Vector2 coverBottom = ImGui.GetItemRectMax();

                ImGui.SetCursorScreenPos(new Vector2(rowStart.X, Math.Max(infoBottom.Y, coverBottom.Y)));
                ImGui.Spacing();
                DrawReplayProgressCard(ui, selected, available.X);
            }
        }

        float actionGap = Math.Max(18f, ImGui.GetStyle().ItemSpacing.Y * 2.5f);
        ImGui.Dummy(new Vector2(1f, actionGap));
        ImGui.Separator();
        DrawReplayDetailsActions(ui, selected);
    }

    private void DrawReplayProgressCard(UiText ui, ReplayFileEntry selected, float width = -1f)
    {
        float cardWidth = width > 0f ? width : ImGui.GetContentRegionAvail().X;
        ImGui.BeginChild("##ReplayProgressCard", new Vector2(cardWidth, 0f),
            ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        int startProgress = GetProgress(selected.StartTile, selected.TotalTiles);
        int endProgress = GetEndProgress(selected);
        Vector4 progressColor = selected.Completed
            ? new Vector4(0.35f, 1f, 0.66f, 1f)
            : new Vector4(1f, 0.72f, 0.35f, 1f);
        ImGui.TextColored(progressColor, $"{ui.Progress}  {startProgress}%  →  {endProgress}%");
        ImGui.ProgressBar(endProgress / 100f, new Vector2(-1f, 16f), $"{endProgress}%");
        DrawManagerMetric(ui.LevelPath, string.IsNullOrWhiteSpace(selected.LevelPath) ? "-" : selected.LevelPath);
        ImGui.EndChild();
    }

    private void DrawReplayDetailsInfo(
        UiText ui,
        ReplayFileEntry selected,
        float progressWidth = -1f)
    {
        float width = progressWidth > 0f ? progressWidth : ImGui.GetContentRegionAvail().X;
        string result = selected.Supported
            ? selected.Completed ? ui.Complete : ui.Failed
            : ui.Unsupported;
        Vector4 resultColor = selected.Supported
            ? selected.Completed ? new Vector4(0.35f, 1f, 0.66f, 1f) : new Vector4(1f, 0.72f, 0.35f, 1f)
            : new Vector4(1f, 0.42f, 0.45f, 1f);

        if (selected.Supported && !string.Equals(_editingReplayPath, selected.Path, StringComparison.Ordinal))
        {
            _editingReplayPath = selected.Path;
            _replayTitleEdit = selected.Title;
            _managerTitleEditing = false;
        }

        ImGui.BeginChild("##ReplayDetailHero", new Vector2(width, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        ImGui.TextColored(resultColor, result.ToUpperInvariant());
        ImGui.SameLine(0f, 12f);
        ImGui.TextUnformatted(GetReplayDisplayTitle(selected));
        if (!string.IsNullOrWhiteSpace(selected.Title))
        {
            ImGui.TextDisabled($"{ui.OriginalSong}: {CleanManagerText(selected.SongName)}");
            if (!string.IsNullOrWhiteSpace(selected.ArtistName))
                ImGui.TextDisabled($"{ui.Artist}: {CleanManagerText(selected.ArtistName)}");
        }
        if (selected.Supported && !_managerTitleEditing)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("编辑标题##detail-edit"))
            {
                _managerTitleEditing = true;
                _replayTitleEdit = selected.Title;
            }
        }
        if (selected.Supported && _managerTitleEditing)
        {
            ImGui.SetNextItemWidth(Math.Max(1f, width - 20f));
            ImGui.InputText("##replay-title-edit", ref _replayTitleEdit, 128);
            bool changed = !string.Equals(_replayTitleEdit.Trim(), selected.Title, StringComparison.Ordinal);
            ImGui.BeginDisabled(!changed);
            if (ImGui.Button(ui.SaveTitle, new Vector2(130f, GetManagerButtonHeight())))
                SaveReplayTitle(selected, ui);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(ui.Cancel, new Vector2(130f, GetManagerButtonHeight())))
            {
                _managerTitleEditing = false;
                _replayTitleEdit = selected.Title;
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.BeginChild("##ReplayDetailMetrics", new Vector2(width, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        float half = Math.Max(1f, (width - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
        ImGui.BeginGroup();
        DrawManagerMetric(ui.RecordedAt, selected.RecordedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        DrawManagerMetric(ui.Inputs, selected.HitCount.ToString());
        ImGui.EndGroup();
        ImGui.SameLine(half + ImGui.GetStyle().ItemSpacing.X);
        ImGui.BeginGroup();
        DrawManagerMetric(ui.Speed, $"{selected.Speed:0.00}x");
        DrawManagerMetric("判定难度", GetDifficultyLabel(selected.Difficulty));
        DrawManagerMetric(ui.Source, selected.IsOfficialLevel ? ui.Official : ui.Custom);
        ImGui.EndGroup();
        ImGui.EndChild();
    }

    private static void DrawManagerMetric(string label, string value)
    {
        ImGui.TextDisabled(label.ToUpperInvariant());
        DrawManagerMutedWrapped(
            string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static bool DrawManagerEditIconButton(string id, float size)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hovered = ImGui.IsItemHovered();
        Vector2 max = min + new Vector2(size, size);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(hovered
            ? new Vector4(0.98f, 0.43f, 0.52f, 1f)
            : new Vector4(0.22f, 0.13f, 0.18f, 1f));
        uint foreground = ImGui.ColorConvertFloat4ToU32(
            new Vector4(1f, 0.90f, 0.94f, 1f));
        drawList.AddRectFilled(min, max, background, 6f);
        Vector2 start = min + new Vector2(size * 0.30f, size * 0.68f);
        Vector2 end = min + new Vector2(size * 0.70f, size * 0.28f);
        drawList.AddLine(start, end, foreground, 2.5f);
        drawList.AddTriangleFilled(
            end,
            end + new Vector2(size * 0.14f, -size * 0.03f),
            end + new Vector2(size * 0.04f, size * 0.14f),
            foreground);
        return clicked;
    }

    private void DrawReplayDetailsActions(UiText ui, ReplayFileEntry selected)
    {
        if (!selected.Supported)
        {
            StopManagerPreview();
            ImGui.TextWrapped(SanitizeImGuiText(selected.Error ?? ui.Unsupported));
        }
        float actionButtonHeight = Math.Max(42f, ImGui.GetFrameHeight() * 1.05f);
        ImGuiStylePtr style = ImGui.GetStyle();
        float actionButtonWidth = selected.Supported
            ? Math.Max(1f, (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) * 0.5f)
            : -1f;
        if (selected.Supported)
        {
            if (ImGui.Button(ui.Play + "##manager-play", new Vector2(actionButtonWidth, actionButtonHeight)))
                QueueReplayFile(selected.Path, ui);
            ImGui.SameLine();
        }
        if (ImGui.Button(
                ui.Delete + "##manager-delete",
                new Vector2(actionButtonWidth, actionButtonHeight)))
        {
            _pendingDeletePath = selected.Path;
            _deletePopupRequested = true;
        }

        if (_deletePopupRequested && string.Equals(_pendingDeletePath, selected.Path, StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextWrapped(ui.ConfirmDelete);
            float confirmButtonWidth = Math.Max(
                1f,
                (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) * 0.5f);
            if (ImGui.Button(
                    ui.Delete + "##manager-confirm-delete",
                    new Vector2(confirmButtonWidth, actionButtonHeight)))
            {
                DeleteReplayFile(selected.Path);
                _deletePopupRequested = false;
            }
            ImGui.SameLine();
            if (ImGui.Button(
                    ui.Cancel + "##manager-cancel-delete",
                    new Vector2(confirmButtonWidth, actionButtonHeight)))
            {
                _pendingDeletePath = "";
                _deletePopupRequested = false;
            }
        }
    }

    private void DrawReplayChartPreview(
        UiText ui,
        ReplayFileEntry selected,
        float maxDisplayHeight = 320f,
        float minCardHeight = 0f)
    {
        EnsureManagerPreview(selected);
        float cardWidth = Math.Max(180f, ImGui.GetContentRegionAvail().X);
        ReplayChartPreview? preview = _replayChartPreview;
        nint textureId = 0;
        int textureWidth = 0;
        int textureHeight = 0;
        bool hasCover = preview != null
            && preview.TryGetTexture(out textureId, out textureWidth, out textureHeight);

        float availableWidth = Math.Max(1f, cardWidth - ImGui.GetStyle().WindowPadding.X * 2f);
        float diameter = Math.Min(Math.Min(availableWidth - 24f, maxDisplayHeight), 280f);
        diameter = Math.Max(148f, diameter);
        float panelHeight = diameter + 12f;
        float cardHeight = Math.Max(panelHeight + 54f, minCardHeight);
        panelHeight = Math.Max(panelHeight, cardHeight - 54f);

        ImGui.BeginChild(
            "##ReplayCoverCard",
            new Vector2(cardWidth, cardHeight),
            ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.TextColored(new Vector4(0.98f, 0.43f, 0.52f, 1f), "谱面封面");
        ImGui.SameLine();
        ImGui.TextDisabled(selected.IsOfficialLevel ? ui.Official : ui.Custom);
        ImGui.Separator();

        Vector2 areaMin = ImGui.GetCursorScreenPos();
        Vector2 areaSize = new(availableWidth, panelHeight);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            areaMin,
            areaMin + areaSize,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.075f, 0.055f, 0.085f, 1f)),
            8f);

        if (hasCover)
        {
            Vector2 min = areaMin + new Vector2(
                (availableWidth - diameter) * 0.5f,
                (panelHeight - diameter) * 0.5f);
            ImGui.Dummy(areaSize);
            DrawRotatingReplayCover(textureId, textureWidth, textureHeight, min, diameter);
        }
        else
        {
            DrawCoverPlaceholder(draw, areaMin, areaSize);
            ImGui.Dummy(areaSize);
        }
        ImGui.EndChild();
    }

    private static void DrawCoverPlaceholder(ImDrawListPtr draw, Vector2 min, Vector2 size)
    {
        Vector2 center = min + size * 0.5f;
        float radius = Math.Min(size.X, size.Y) * 0.18f;
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.74f, 0.48f, 0.62f, 1f));
        uint muted = ImGui.ColorConvertFloat4ToU32(new Vector4(0.48f, 0.35f, 0.46f, 1f));
        draw.AddCircle(center, radius, color, 32, 2.5f);
        draw.AddCircleFilled(center, radius * 0.18f, color, 16);
        draw.AddLine(center + new Vector2(0, -radius * 0.75f), center + new Vector2(0, radius * 0.75f), muted, 2f);
        draw.AddLine(center + new Vector2(-radius * 0.75f, 0), center + new Vector2(radius * 0.75f, 0), muted, 2f);
    }

    private static void DrawRotatingReplayCover(
        nint textureId,
        int textureWidth,
        int textureHeight,
        Vector2 min,
        float diameter)
    {
        Vector2 center = min + new Vector2(diameter * 0.5f);
        float radius = diameter * 0.5f;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.30f));
        drawList.AddCircleFilled(center, radius + 3f, shadow, ChartCoverSegmentCount);

        double elapsedSeconds = Environment.TickCount64 / 1000d;
        float rotation = (float)((elapsedSeconds * ChartCoverRotationSpeedDegreesPerSecond
            % 360d) * (Math.PI / 180d));
        float uRadius = textureWidth > textureHeight
            ? textureHeight / (float)textureWidth * 0.5f
            : 0.5f;
        float vRadius = textureHeight > textureWidth
            ? textureWidth / (float)textureHeight * 0.5f
            : 0.5f;
        Vector2 centerUv = new(0.5f, 0.5f);
        Vector2 previousPoint = CirclePoint(center, radius, rotation);
        Vector2 previousUv = CoverUv(0f, uRadius, vRadius);
        for (int segment = 1; segment <= ChartCoverSegmentCount; segment++)
        {
            float angle = segment * (MathF.PI * 2f / ChartCoverSegmentCount);
            Vector2 point = CirclePoint(center, radius, angle + rotation);
            Vector2 uv = CoverUv(angle, uRadius, vRadius);
            drawList.AddImageQuad(
                (IntPtr)textureId,
                center,
                previousPoint,
                point,
                center,
                centerUv,
                previousUv,
                uv,
                centerUv);
            previousPoint = point;
            previousUv = uv;
        }

        uint rim = ImGui.ColorConvertFloat4ToU32(new Vector4(0.70f, 0.86f, 1f, 0.85f));
        drawList.AddCircle(center, radius, rim, ChartCoverSegmentCount, 1.5f);
    }

    private static Vector2 CirclePoint(Vector2 center, float radius, float angle)
        => center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

    private static Vector2 CoverUv(float angle, float uRadius, float vRadius)
        => new(
            0.5f + MathF.Cos(angle) * uRadius,
            0.5f - MathF.Sin(angle) * vRadius);

    private static float GetManagerButtonHeight()
    {
        return Math.Max(48f, ImGui.GetFrameHeight() * 1.12f);
    }

    private static bool CanFitManagerButtonRow(float availableWidth, params string[] labels)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float requiredWidth = style.ItemSpacing.X * Math.Max(0, labels.Length - 1);
        foreach (string label in labels)
        {
            requiredWidth += Math.Max(
                112f,
                ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f + 20f);
        }
        return availableWidth >= requiredWidth;
    }

    private static void DrawManagerSectionTitle(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.98f, 0.43f, 0.52f, 1f), text);
    }

    private static void DrawManagerMutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.86f, 0.80f, 0.86f, 1f));
        ImGui.TextWrapped(SanitizeImGuiText(text));
        ImGui.PopStyleColor();
    }

    private static string GetReplayDisplayTitle(ReplayFileEntry entry)
    {
        return CleanManagerText(
            entry.DisplayTitle,
            stripRichText: string.IsNullOrWhiteSpace(entry.Title));
    }

    private static string CleanManagerText(string value, bool stripRichText = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        StringBuilder builder = new(value.Length);
        bool insideTag = false;
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (stripRichText && character == '<')
            {
                insideTag = true;
                continue;
            }
            if (insideTag)
            {
                if (character == '>')
                    insideTag = false;
                continue;
            }
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
        }
        return SanitizeImGuiText(builder.ToString().TrimEnd());
    }

    private static string SanitizeImGuiText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        StringBuilder builder = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    index++;
                builder.Append('*');
            }
            else if (char.IsLowSurrogate(character))
            {
                builder.Append('*');
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private static string EllipsizeManagerText(string text, float maximumWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maximumWidth)
            return text;

        const string suffix = "...";
        float suffixWidth = ImGui.CalcTextSize(suffix).X;
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            float width = ImGui.CalcTextSize(text[..middle]).X + suffixWidth;
            if (width <= maximumWidth)
                low = middle;
            else
                high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(text[low - 1]))
            low--;
        return text[..low].TrimEnd() + suffix;
    }

    private void QueueReplayFile(string path, UiText ui)
    {
        try
        {
            ReplayData replay = RequireStore().Load(path);
            CloseReplayManager();
            _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Play, replay));
            SetNotice(ui.Queued);
        }
        catch (Exception exception)
        {
            SetNotice($"{ui.LoadFailed}: {exception.Message}");
        }
    }

    private void SaveReplayTitle(ReplayFileEntry selected, UiText ui)
    {
        try
        {
            string title = RequireStore().UpdateTitle(selected.Path, _replayTitleEdit);
            _editingReplayPath = selected.Path;
            _replayTitleEdit = title;
            _managerTitleEditing = false;
            SetNotice(ui.TitleSaved);
            SetToast(ui.TitleSaved);
            RefreshFiles();
        }
        catch (Exception exception)
        {
            SetNotice(exception.Message);
            Logger.Error(LogTag, $"Update replay title failed: {exception}");
        }
    }

    private void DeleteReplayFile(string path)
    {
        try
        {
            File.Delete(path);
            _selectedReplayPath = "";
            _pendingDeletePath = "";
            _editingReplayPath = "";
            _replayTitleEdit = "";
            _managerTitleEditing = false;
            _managerShowingDetails = false;
            RefreshFiles();
        }
        catch (Exception exception)
        {
            SetNotice(exception.Message);
        }
    }

    private static int GetEndProgress(ReplayFileEntry entry)
    {
        return GetProgress(entry.EndTile, entry.TotalTiles);
    }

    private static int GetProgress(int tile, int totalTiles)
    {
        if (totalTiles <= 1)
            return 0;
        return Math.Clamp((int)Math.Round(tile * 100d / (totalTiles - 1)), 0, 100);
    }

    private void DrawHud(ImDrawListPtr drawList)
    {
        NormalizeSettings();
        if (!_showReplayHud)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        string text;
        ReplayRunState state;
        bool recording;
        lock (_stateLock)
        {
            state = _runState;
            recording = _recording;
            if (!recording && _activeReplay == null)
                return;
            text = GetStatusTextLocked(ui, includeSong: true);
        }
        if (string.IsNullOrEmpty(text))
            return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0f || display.Y <= 0f)
            return;
        Vector2 position = new(_hudPositionX * display.X, _hudPositionY * display.Y);
        ImFontPtr font = ImGui.GetFont();
        uint color = state switch
        {
            ReplayRunState.Playing => ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.85f, 1f, 1f)),
            ReplayRunState.Paused => ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.25f, 1f)),
            ReplayRunState.Failed => ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.35f, 0.25f, 1f)),
            ReplayRunState.Finished => ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 1f, 0.45f, 1f)),
            _ when recording => ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.3f, 0.3f, 1f)),
            _ => 0xFFFFFFFF,
        };
        drawList.AddText(font, _hudFontSize, position + new Vector2(2f, 2f), 0xC0000000, text);
        drawList.AddText(font, _hudFontSize, position, color, text);
    }

    private string GetStatusText(UiText ui, bool includeSong)
    {
        lock (_stateLock)
            return GetStatusTextLocked(ui, includeSong);
    }

    private string GetStatusTextLocked(UiText ui, bool includeSong)
    {
        ReplayData? replay = _activeReplay ?? (_runState == ReplayRunState.Loading ? _pendingReplay : null);
        string prefix = _runState switch
        {
            ReplayRunState.Loading => ui.ReplayLoading,
            ReplayRunState.WaitingForStart => ui.ReplayWaiting,
            ReplayRunState.Playing => ui.Replaying,
            ReplayRunState.Paused => ui.ReplayPaused,
            ReplayRunState.Finished => ui.ReplayFinished,
            ReplayRunState.Failed => ui.ReplayFailed,
            _ when _recording => ui.Recording,
            _ => "",
        };
        if (string.IsNullOrEmpty(prefix))
            return "";

        ReplayData? data = replay ?? _currentAttempt;
        int count = replay == null ? data?.Hits.Count ?? 0 : Math.Min(_replayIndex, replay.Hits.Count);
        string progress = replay == null
            ? $"{count} {ui.Hits}"
            : $"{count}/{replay.Hits.Count} {ui.Hits}";
        // 谱面标题里常带 Unity 富文本标签（例如 `비밀 인형극 II</color>`）和换行，
        // 直接画出来会把标签当成正文显示。这里和回放管理器使用同一套清洗规则，
        // 保证录制中／回放中的 HUD 只显示纯文本歌曲名。
        string cleanedSong = includeSong && data != null ? CleanManagerText(data.SongName) : "";
        // 标题被标签占满时清洗结果可能为空，此时不要留下多余的空格。
        string song = string.IsNullOrEmpty(cleanedSong) ? "" : $"  {cleanedSong}";
        return $"{prefix}  {progress}{song}";
    }

    private static bool DrawIconButton(string icon, string id, Vector2 size)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Vector4 fill = hovered ? new Vector4(0.38f, 0.17f, 0.27f, 1f) : new Vector4(0.18f, 0.12f, 0.18f, 1f);
        draw.AddRectFilled(min, min + size, ImGui.ColorConvertFloat4ToU32(fill), 7f);
        Vector2 center = min + size * 0.5f;
        uint ink = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.88f, 0.93f, 1f));
        float r = Math.Min(size.X, size.Y) * 0.24f;
        switch (icon)
        {
            case "close":
                draw.AddLine(center + new Vector2(-r, -r), center + new Vector2(r, r), ink, Math.Max(2.5f, Math.Min(size.X, size.Y) * 0.055f));
                draw.AddLine(center + new Vector2(r, -r), center + new Vector2(-r, r), ink, Math.Max(2.5f, Math.Min(size.X, size.Y) * 0.055f));
                break;
            case "back":
                draw.AddLine(center + new Vector2(r, 0), center + new Vector2(-r, 0), ink, Math.Max(2.5f, Math.Min(size.X, size.Y) * 0.055f));
                draw.AddLine(center + new Vector2(-r, 0), center + new Vector2(-r * 0.15f, -r * 0.85f), ink, Math.Max(2.5f, Math.Min(size.X, size.Y) * 0.055f));
                draw.AddLine(center + new Vector2(-r, 0), center + new Vector2(-r * 0.15f, r * 0.85f), ink, Math.Max(2.5f, Math.Min(size.X, size.Y) * 0.055f));
                break;
            case "refresh":
                draw.AddCircle(center, r, ink, 20, 2.5f);
                draw.AddTriangleFilled(center + new Vector2(r, -r * 0.9f), center + new Vector2(r * 0.25f, -r * 1.15f), center + new Vector2(r * 0.65f, -r * 0.35f), ink);
                break;
        }
        return clicked;
    }

    private static void DrawNoticeIcon(ImDrawListPtr draw, Vector2 position)
    {
        float size = Math.Max(18f, ImGui.GetTextLineHeight() * 0.9f);
        Vector2 center = position + new Vector2(size * 0.5f, size * 0.5f);
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.98f, 0.43f, 0.52f, 1f));
        draw.AddCircle(center, size * 0.38f, color, 20, 2f);
        draw.AddLine(center + new Vector2(0, -size * 0.18f), center + new Vector2(0, size * 0.10f), color, 2f);
        draw.AddCircleFilled(center + new Vector2(0, size * 0.23f), 1.5f, color);
        ImGui.Dummy(new Vector2(size, size));
    }

    private void ShowNotice()
    {
        string notice;
        DateTime until;
        lock (_stateLock)
        {
            notice = _notice;
            until = _noticeUntilUtc;
        }
        if (!string.IsNullOrEmpty(notice) && DateTime.UtcNow < until)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.18f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.96f, 0.90f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
            if (ImGui.BeginChild("##ReplayNotice", new Vector2(-1f, 0f), ImGuiChildFlags.AutoResizeY))
                DrawNoticeIcon(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos());
                ImGui.SameLine(0f, 10f);
                ImGui.TextWrapped(SanitizeImGuiText(notice));
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }
    }

    private void SetNotice(string message)
    {
        lock (_stateLock)
        {
            _notice = message;
            _noticeUntilUtc = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private void SetToast(string message)
    {
        lock (_stateLock)
        {
            _toast = message;
            _toastUntilUtc = DateTime.UtcNow.AddSeconds(4);
        }
    }

    private void NormalizeSettings()
    {
        _hudFontSize = Math.Clamp(_hudFontSize, 12, 64);
        _hudPositionX = Math.Clamp(_hudPositionX, 0f, 1f);
        _hudPositionY = Math.Clamp(_hudPositionY, 0f, 1f);
        _maximumSavedReplays = Math.Clamp(_maximumSavedReplays, 1, 500);
        _replayDirectory ??= "";
    }

    private void SyncInputReceivers()
    {
        bool receiveTouch = _loaded && _receiveTouchInput;
        if (receiveTouch != _touchInputSubscribed)
        {
            if (receiveTouch)
                InputEvents.OnTouch += OnTouch;
            else
                InputEvents.OnTouch -= OnTouch;
            _touchInputSubscribed = receiveTouch;
        }

        bool receiveKeyboard = _loaded && _receiveKeyboardInput;
        if (receiveKeyboard == _keyboardInputActive)
            return;

        _keyboardInputActive = receiveKeyboard;
        if (receiveKeyboard)
        {
            // One opt-in attempt is enough. On devices without this native
            // symbol, the recorder uses its Unity/ImGui fallback while recording.
            ReplayKeyboardHook.Install(this);
        }
        else
        {
            ReplayKeyboardHook.Uninstall();
            ReplayKeyboardRecorder.Reset();
        }
    }

    private void LoadSettings()
    {
        try
        {
            ReplaySettings settings = RequireStore().LoadSettings();
            bool currentSettings = settings.SettingsVersion >= 2;
            bool inputSettings = settings.SettingsVersion >= 3;
            _saveFullClear = currentSettings ? settings.SaveFullClear : true;
            _saveEveryCompletion = currentSettings && settings.SaveEveryCompletion;
            _saveEveryFailure = currentSettings && settings.SaveEveryFailure;
            _saveFailureAt90Percent = !currentSettings || settings.SaveFailureAt90Percent;
            _disableTutorialAutoSave = !currentSettings || settings.DisableTutorialAutoSave;
            _ignoreAutoplay = settings.IgnoreAutoplay;
            _showReplayHud = settings.ShowReplayHud;
            _receiveTouchInput = inputSettings ? settings.ReceiveTouchInput : true;
            _receiveKeyboardInput = inputSettings && settings.ReceiveKeyboardInput;
            _hudFontSize = settings.HudFontSize;
            _hudPositionX = settings.HudPositionX;
            _hudPositionY = settings.HudPositionY;
            _maximumSavedReplays = settings.MaximumSavedReplays;
            _replayDirectory = settings.ReplayDirectory ?? "";
            NormalizeSettings();
        }
        catch (Exception exception)
        {
            Logger.Warn(LogTag, $"Could not load replay settings; defaults will be used: {exception.Message}");
            NormalizeSettings();
        }
    }

    private void SaveSettings()
    {
        ReplayStore? store = _store;
        if (store == null)
            return;
        try
        {
            NormalizeSettings();
            store.SaveSettings(new ReplaySettings
            {
                SettingsVersion = 5,
                SaveFullClear = _saveFullClear,
                SaveEveryCompletion = _saveEveryCompletion,
                SaveEveryFailure = _saveEveryFailure,
                SaveFailureAt90Percent = _saveFailureAt90Percent,
                DisableTutorialAutoSave = _disableTutorialAutoSave,
                IgnoreAutoplay = _ignoreAutoplay,
                ShowReplayHud = _showReplayHud,
                ReceiveTouchInput = _receiveTouchInput,
                ReceiveKeyboardInput = _receiveKeyboardInput,
                HudFontSize = _hudFontSize,
                HudPositionX = _hudPositionX,
                HudPositionY = _hudPositionY,
                MaximumSavedReplays = _maximumSavedReplays,
                ReplayDirectory = _replayDirectory,
            });
        }
        catch (Exception exception)
        {
            SetNotice(exception.Message);
            Logger.Error(LogTag, $"Save replay settings failed: {exception}");
        }
    }

    private void EnsureAttemptStarted(nint controller)
    {
        GameApi? game = _game;
        bool editorPlay;
        lock (_stateLock)
        {
            editorPlay = _editorPlayRequested;
        }
        // Start_Rewind 被调用过说明游戏已确认要开始游玩，不管 state 是 0 还是 4 都应该录制。
        bool pendingStart;
        lock (_stateLock)
            pendingStart = _pendingAttemptController != 0;
        if (game == null || controller == 0
            || (!editorPlay && !game.IsGameWorld(controller))
            || (!editorPlay && !pendingStart && game.GetControllerState(controller) != PlayerControlState))
            return;

        lock (_stateLock)
        {
            if (_recording || _activeReplay != null || _pendingReplay != null || _currentAttempt != null)
                return;
            if (!editorPlay && _levelTransitionInProgress)
                return;
            if (_editorFinalized)
                return;
            if (_pendingAttemptController != 0 && _pendingAttemptController != controller)
            {
                _pendingAttemptController = controller;
                _pendingIdentity = null;
                _identityStableTicks = 0;
            }
        }

        if (!game.TryGetLevelIdentity(controller, out ReplayLevelIdentity? identity, out _, editorPlay)
            || identity == null)
        {
            lock (_stateLock)
            {
                _pendingIdentity = null;
                _identityStableTicks = 0;
            }
            return;
        }

        int startTile;
        lock (_stateLock)
        {
            if (_pendingIdentity?.StableKey == identity.StableKey)
                _identityStableTicks++;
            else
            {
                _pendingIdentity = identity;
                _identityStableTicks = 1;
            }
            if (_identityStableTicks < StableIdentityTicksRequired)
                return;
            // 起始砖在 QueueAttemptStart 时就已按 GCS.checkpointNum 解析过，这里沿用该结果，
            // 避免等待关卡身份稳定的这段时间里 checkpointNum 变化导致起点漂移。
            startTile = _pendingAttemptStartTile >= 0
                ? _pendingAttemptStartTile
                : ResolveAttemptStartTile(game, controller, -1);
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
        }
        BeginAttempt(controller, startTile, identity, editorPlay);
    }



    /// <summary>
    /// 解析本局录制真正的起始砖。
    /// 游戏里权威来源是 <c>GCS.checkpointNum</c>：<c>Start_Rewind</c> 常规路径的参数是 <c>-1</c>
    /// （表示“沿用当前 checkpointNum”），因此不能直接把该参数当成起点，否则从检查点续关的录制
    /// 会被记成 <c>StartTile = 0</c>，既让“从第一砖通关才保存”的策略误触发，也让回放从头播放。
    /// </summary>
    private int ResolveAttemptStartTile(GameApi game, nint controller, int requestedStartTile)
    {
        int checkpoint = game.GetCheckpoint();
        if (checkpoint > 0)
            return checkpoint;
        if (requestedStartTile > 0)
            return requestedStartTile;
        int currentSequence = game.GetCurrentSequence(controller);
        return currentSequence > 0 ? currentSequence : 0;
    }

    private bool QueueAttemptStart(nint controller, int startTile)
    {
        // 起始砖在这里就地解析：此时 Start_Rewind 的原函数已经跑完，GCS.checkpointNum 与
        // 本局真实起点一致（PC 版同样在 Start_Rewind 的 postfix 里读取该字段）。
        int resolved = _game is { } game
            ? ResolveAttemptStartTile(game, controller, startTile)
            : Math.Max(0, startTile);
        lock (_stateLock)
        {
            if (_activeReplay != null || _pendingReplay != null)
                return false;
            _currentAttempt = null;
            _recording = false;
            _recordingStartTicks = 0;
            _pendingAttemptController = controller;
            _pendingAttemptStartTile = resolved;
            _pendingIdentity = null;
            _identityStableTicks = 0;
            return true;
        }
    }

    private void SetResultAttempt(ReplayData replay, bool saved)
    {
        lock (_stateLock)
        {
            _resultAttempt = CloneReplay(replay);
            _resultAttemptSaved = saved;
            _resultSaveQueued = false;
            _resultSaveButtonUntilUtc = saved
                ? DateTime.UtcNow.AddSeconds(5)
                : default;
        }
    }

    private void ClearResultAttemptLocked()
    {
        _resultAttempt = null;
        _resultAttemptSaved = false;
        _resultSaveQueued = false;
        _resultSaveButtonUntilUtc = default;
    }

    private static bool IsEditorReplay(ReplayData? replay)
    {
        return replay != null
            && string.Equals(replay.SceneName, "scnEditor", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(replay.LevelPath);
    }

    private static bool IsCustomReplayExitCandidate(ReplayData replay)
    {
        return !replay.IsOfficialLevel
            && !IsEditorReplay(replay)
            && !string.IsNullOrWhiteSpace(replay.LevelPath);
    }

    private static ReplayData? CloneReplay(ReplayData? source)
    {
        if (source == null)
            return null;
        return new ReplayData
        {
            FormatVersion = source.FormatVersion,
            ModVersion = source.ModVersion,
            SessionId = source.SessionId,
            RecordedAtUtc = source.RecordedAtUtc,
            SongName = source.SongName,
            Title = source.Title,
            ArtistName = source.ArtistName,
            LevelPath = source.LevelPath,
            SceneName = source.SceneName,
            LevelId = source.LevelId,
            IsOfficialLevel = source.IsOfficialLevel,
            Completed = source.Completed,
            Speed = source.Speed,
            Bpm = source.Bpm,
            Difficulty = source.Difficulty,
            StartTile = source.StartTile,
            EndTile = source.EndTile,
            TotalTiles = source.TotalTiles,
            Hits = source.Hits.Select(hit => new ReplayHit
            {
                SequenceId = hit.SequenceId,
                HitAngleOffset = hit.HitAngleOffset,
                HitMargin = hit.HitMargin,
                NoFailHit = hit.NoFailHit,
                AutoHit = hit.AutoHit,
            }).ToList(),
            TouchEvents = (source.TouchEvents ?? new List<ReplayTouchInput>())
                .Where(input => input != null)
                .Select(input => new ReplayTouchInput
                {
                    TimeMilliseconds = input.TimeMilliseconds,
                    Action = input.Action,
                    PointerId = input.PointerId,
                    X = input.X,
                    Y = input.Y,
                    SourceWidth = input.SourceWidth,
                    SourceHeight = input.SourceHeight,
                })
                .ToList(),
            KeyboardEvents = (source.KeyboardEvents ?? new List<ReplayKeyboardInput>())
                .Where(input => input != null)
                .OrderBy(input => input.TimeMilliseconds)
                .Select(input => new ReplayKeyboardInput
                {
                    TimeMilliseconds = input.TimeMilliseconds,
                    Binding = input.Binding ?? "",
                    Action = input.Action,
                    Repeat = input.Repeat,
                })
                .ToList(),
        };
    }

    private ReplayData? GetLatestAttemptLocked()
    {
        if (_currentAttempt is { Hits.Count: > 0 } current
            && (_lastAttempt == null || current.RecordedAtUtc >= _lastAttempt.RecordedAtUtc))
            return current;
        return _lastAttempt;
    }
}
