using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace Replay.Mobile;

public sealed class ReplayPlugin : IModPlugin, IModSettings
{
    private const string LogTag = "Replay";
    private const int PlayerControlState = 4;
    private const int StableIdentityTicksRequired = 20;
    private static readonly TimeSpan CustomLevelBrowserTimeout = TimeSpan.FromSeconds(60);

    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<ReplayCommand> _commands = new();
    private readonly HashSet<string> _autoSavedSessions = new(StringComparer.Ordinal);

    private GameApi? _game;
    private ReplayStore? _store;
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
    private int _pendingAttemptStartTile = -1;
    private int _identityStableTicks;
    private int _languageCode = 10;
    private ReplayLevelIdentity? _pendingIdentity;
    private string _lastScannedDirectory = "";
    private string _selectedReplayPath = "";
    private string _editingReplayPath = "";
    private string _replayTitleEdit = "";
    private string _fileSearch = "";
    private string _pendingDeletePath = "";
    private bool _recording;
    private bool _levelTransitionInProgress;
    private bool _renderErrorLogged;
    private bool _managerOpen;
    private bool _managerShowingDetails;
    private bool _managerPausedGame;
    private bool _resultAttemptSaved;
    private bool _resultSaveQueued;
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
    private bool _ignoreAutoplay = true;
    private bool _showReplayHud = true;
    private int _hudFontSize = 24;
    private float _hudPositionX = 0.02f;
    private float _hudPositionY = 0.08f;
    private int _maximumSavedReplays = 100;
    private string _replayDirectory = "";

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
            return typeof(ReplayPlugin).Assembly.GetName().Version?.ToString() ?? "1.4.2";
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
        _store = new ReplayStore(modDirectory);
        LoadSettings();
        _game = GameApi.Create();
        if (_game == null)
            throw new InvalidOperationException("ADOFAI IL2CPP runtime or Assembly-CSharp was not found");

        Logger.Info(
            LogTag,
            $"Runtime custom-level loader available: {_game.CanLoadScenes}");
        _languageCode = _game.GetLanguageCode();
        if (!GameHooks.Install(this, _game))
        {
            _game = null;
            throw new InvalidOperationException("Required Replay IL2CPP hooks could not be installed");
        }
        CustomLoadDiagnostics.Install(this);

        RefreshFiles();
        nint controller = _game.GetController();
        if (_game.IsGameWorld(controller))
            QueueAttemptStart(controller, _game.GetCurrentSequence(controller));
        Logger.Info(LogTag, "Loaded for StArray.ModManager 1.0.4+");
    }

    public void OnUnload()
    {
        SaveSettings();
        CloseReplayManager();
        try
        {
            StopPlaybackNow();
        }
        catch
        {
        }
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
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
            _resultAttemptSaved = false;
            _resultSaveQueued = false;
            _toast = "";
            _toastUntilUtc = default;
        }
        Logger.Info(LogTag, "Unloaded");
    }

    public void OnForegroundGUI(ImDrawListPtr drawList)
    {
        try
        {
            DrawHud(drawList);
            DrawReplayControls();
            DrawResultSaveButton();
            DrawIslandEntry();
            DrawReplayManager();
            DrawToast();
        }
        catch (Exception exception)
        {
            if (_renderErrorLogged)
                return;
            _renderErrorLogged = true;
            Logger.Error(LogTag, $"HUD render failed: {exception}");
        }
    }

    public void OnGui()
    {
        Interlocked.Exchange(ref _lastSettingsGuiTick, Environment.TickCount64);
        NormalizeSettings();
        TickConductorMainThread();
        UiText ui = UiText.FromLanguage(_languageCode);
        if (!string.Equals(_lastScannedDirectory, _replayDirectory, StringComparison.Ordinal))
            RefreshFiles();

        string status = GetStatusText(ui, includeSong: true);
        if (!string.IsNullOrEmpty(status))
            ImGui.TextWrapped(status);
        ShowNotice();

        ImGui.Separator();
        if (ImGui.Button(ui.OpenManager))
            OpenReplayManager();

        ImGui.Separator();
        ImGui.TextUnformatted(ui.SaveOptions);
        bool settingsChanged = false;
        settingsChanged |= ImGui.Checkbox(ui.SaveFullClear, ref _saveFullClear);
        settingsChanged |= ImGui.Checkbox(ui.SaveEveryCompletion, ref _saveEveryCompletion);
        settingsChanged |= ImGui.Checkbox(ui.SaveEveryFailure, ref _saveEveryFailure);
        settingsChanged |= ImGui.Checkbox(ui.SaveFailureAt90Percent, ref _saveFailureAt90Percent);
        settingsChanged |= ImGui.Checkbox(ui.DisableAutoReplay, ref _ignoreAutoplay);
        settingsChanged |= ImGui.Checkbox(ui.ShowHud, ref _showReplayHud);
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
            SaveSettings();
        }

        ImGui.Separator();
        if (ImGui.Button(ui.SaveCurrent))
            QueueSaveCurrent(ui);
        ImGui.SameLine();
        if (ImGui.Button(ui.PlayLast))
            QueuePlayLast(ui);

        ReplayRunState runState;
        lock (_stateLock)
            runState = _runState;
        if (runState is ReplayRunState.Playing or ReplayRunState.Paused
            or ReplayRunState.WaitingForStart or ReplayRunState.Finished or ReplayRunState.Failed)
        {
            if (runState is ReplayRunState.Playing or ReplayRunState.Paused)
            {
                if (ImGui.Button(runState == ReplayRunState.Paused ? ui.Resume : ui.Pause))
                    _commands.Enqueue(new ReplayCommand(ReplayCommandKind.TogglePause));
                ImGui.SameLine();
            }
            if (ImGui.Button(ui.Stop))
                _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
        }
    }

    internal bool ShouldBlockPlayerHit(nint player)
    {
        lock (_stateLock)
            return _activeReplay != null || _managerOpen;
    }

    internal bool ShouldBlockInput()
    {
        lock (_stateLock)
        {
            return _managerOpen
                || _activeReplay != null && _runState is ReplayRunState.Playing
                    or ReplayRunState.Paused
                    or ReplayRunState.Finished
                    or ReplayRunState.Failed;
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

        _controller = controller;
        _levelTransitionInProgress = false;
        bool activated = false;
        bool restarted = false;

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
                activated = true;
            }
            else if (_activeReplay != null
                && _runState is not ReplayRunState.Finished and not ReplayRunState.Failed)
            {
                _replayIndex = 0;
                _recording = false;
                _runState = ReplayRunState.WaitingForStart;
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
            _game?.CancelPendingCustomReplayLoad();
            Logger.Info(LogTag, "Replay activated after level load");
            return;
        }
        if (restarted)
            return;

        QueueAttemptStart(controller, sequenceId);
    }

    internal void HandleLevelLoadStarted(nint controller)
    {
        ReplayData? discarded;
        lock (_stateLock)
        {
            discarded = _currentAttempt;
            _currentAttempt = null;
            _recording = false;
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

    internal void TickMainThread(nint controller)
    {
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
            }
        }
        AdvancePendingReplayLoad();
        EnsureAttemptStarted(controller);
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
        }
        Logger.Info(LogTag, $"Replay custom level opened via {game?.LastLoadRoute}: {game?.GetReplayLoadState()}");
    }

    private void FailPendingReplayLoad(string error)
    {
        _game?.CancelPendingCustomReplayLoad();
        lock (_stateLock)
        {
            _activeReplay = null;
            _pendingReplay = null;
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
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

        nint controller = game.GetController();
        nint player = game.GetPlayer(controller);
        if (!game.IsGameWorld(controller) || player == 0)
            return;
        _controller = controller;
        _player = player;

        if (game.GetControllerState(controller) != PlayerControlState || game.IsPaused(controller))
            return;
        if (state == ReplayRunState.WaitingForStart)
        {
            lock (_stateLock)
                _runState = ReplayRunState.Playing;
        }

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
            try
            {
                GameHooks.InjectPlayerHit(player, autoHit: true, midSpin ? 3 : hit.HitMargin);
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

    internal bool HandleFail(nint controller)
    {
        ReplayData? replay;
        lock (_stateLock)
            replay = _activeReplay;
        if (replay != null)
        {
            lock (_stateLock)
                _runState = ReplayRunState.Failed;
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

    internal void ReleaseReplayAfterResult(string result)
    {
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
            _runState = ReplayRunState.Idle;
        }
        if (released)
            Logger.Info(LogTag, $"Replay state released after {result}");
    }

    private void BeginAttempt(nint controller, int startTile, ReplayLevelIdentity identity)
    {
        GameApi? game = _game;
        if (game == null || !game.IsGameWorld(controller))
            return;
        nint player = game.GetPlayer(controller);
        if (player == 0 || _ignoreAutoplay && game.IsPlayerAuto(player))
            return;

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
            _runState = ReplayRunState.Idle;
        }
        Logger.Info(
            LogTag,
            $"Recording started: {attempt.SongName}, official={attempt.IsOfficialLevel}, "
            + $"level='{attempt.LevelId}', path='{attempt.LevelPath}', tile {attempt.StartTile}");
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
                return null;
            }
            if (_currentAttempt.Hits.Count == 0)
            {
                Logger.Warn(LogTag, $"Recording ended without captured hits: {_currentAttempt.SongName}");
                _currentAttempt = null;
                _recording = false;
                return null;
            }

            _currentAttempt.Completed = completed;
            _currentAttempt.EndTile = Math.Max(
                _currentAttempt.EndTile,
                game?.GetCurrentSequence(controller) ?? _currentAttempt.EndTile);
            _lastAttempt = CloneReplay(_currentAttempt);
            finalized = CloneReplay(_lastAttempt);
            _currentAttempt = null;
            _recording = false;
        }
        if (finalized == null)
            return null;
        Logger.Info(LogTag, $"Recording finalized: {finalized.Hits.Count} hits, completed={completed}");
        return finalized;
    }

    private void FinishExhaustedReplay(ReplayData replay, nint controller)
    {
        if (!replay.Completed || (_game?.GetCurrentSequence(controller) ?? 0) < replay.EndTile)
            return;
        lock (_stateLock)
            _runState = ReplayRunState.Finished;
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
        return replay.Completed
            ? _saveEveryCompletion || _saveFullClear && replay.StartTile == 0
            : _saveEveryFailure || _saveFailureAt90Percent && progress >= 0.9f;
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
                    _resultAttemptSaved = true;
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
        ReplayData pendingReplay = CloneReplay(replay)!;
        RepairLegacyStartTile(pendingReplay);
        lock (_stateLock)
        {
            ClearResultAttemptLocked();
            _activeReplay = null;
            _pendingReplay = pendingReplay;
            _currentAttempt = null;
            _runState = ReplayRunState.Loading;
            _loadStage = ReplayLoadStage.WaitingForTargetStart;
            _loadDeadlineUtc = DateTime.UtcNow.Add(CustomLevelBrowserTimeout);
            _replayIndex = 0;
            _recording = false;
        }
        Logger.Info(
            LogTag,
            $"Loading replay: {replay.SongName}, {replay.Hits.Count} hits, "
            + $"official={replay.IsOfficialLevel}, level='{replay.LevelId}', "
            + $"scene='{replay.SceneName}', path='{replay.LevelPath}'");

        if (!game.LoadReplayLevel(pendingReplay))
        {
            lock (_stateLock)
            {
                _activeReplay = null;
                _pendingReplay = null;
                _runState = ReplayRunState.Idle;
                _loadStage = ReplayLoadStage.None;
                _loadDeadlineUtc = default;
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
                _loadStage = game.WaitingForCustomLevelBrowser
                    ? ReplayLoadStage.WaitingForCustomLevelBrowser
                    : game.WaitingForLevelSelect
                        ? ReplayLoadStage.WaitingForLevelSelect
                        : ReplayLoadStage.WaitingForTargetStart;
        }
        Logger.Info(LogTag, $"Replay load requested via {game.LastLoadRoute}: {game.GetReplayLoadState()}");
    }

    private void TogglePauseNow()
    {
        GameApi? game = _game;
        if (game == null)
            return;
        nint controller = game.GetController();
        lock (_stateLock)
        {
            if (_runState == ReplayRunState.Playing)
            {
                game.SetPaused(controller, true);
                _runState = ReplayRunState.Paused;
            }
            else if (_runState == ReplayRunState.Paused)
            {
                game.SetPaused(controller, false);
                _runState = ReplayRunState.Playing;
            }
        }
    }

    private void StopPlaybackNow(bool resumeRecording = true)
    {
        GameApi? game = _game;
        game?.CancelPendingCustomReplayLoad();
        nint controller = game?.GetController() ?? 0;
        if (controller != 0)
            game?.SetPaused(controller, false);
        lock (_stateLock)
        {
            _activeReplay = null;
            _pendingReplay = null;
            _replayIndex = 0;
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
            _loadDeadlineUtc = default;
        }
        if (resumeRecording && game?.IsGameWorld(controller) == true)
            QueueAttemptStart(controller, game.GetCurrentSequence(controller));
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
        }
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
        if (controller == 0 || game?.IsGameWorld(controller) != true || game.IsPaused(controller))
            return;

        game.SetPaused(controller, true);
        lock (_stateLock)
        {
            if (_managerOpen)
            {
                _managerPausedGame = true;
                return;
            }
        }
        game.SetPaused(controller, false);
    }

    private void ResumeAfterReplayManagerNow()
    {
        GameApi? game = _game;
        nint controller = game?.GetController() ?? 0;
        if (controller != 0)
            game?.SetPaused(controller, false);
    }

    private void DrawIslandEntry()
    {
        GameApi? game = _game;
        bool customLevelSelect = game?.IsCustomLevelSelect() == true;
        bool entryScene = customLevelSelect || game?.IsLevelSelect() == true;
        if (!entryScene)
            _islandEntryLogged = false;
        long settingsAge = Environment.TickCount64 - Interlocked.Read(ref _lastSettingsGuiTick);
        ImGuiIOPtr io = ImGui.GetIO();
        if (!entryScene || game == null || _managerOpen || settingsAge < 250 || io.WantTextInput)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        Vector2 display = io.DisplaySize;
        if (display.X < 200f || display.Y < 120f)
            return;
        ImGuiStylePtr style = ImGui.GetStyle();
        float margin = GetOverlayMargin();
        float buttonHeight = GetOverlayButtonHeight();
        float desiredWidth = ImGui.CalcTextSize(ui.IslandEntry).X
            + style.FramePadding.X * 2f
            + style.WindowPadding.X * 2f;
        float width = ClampOverlayWidth(display.X, margin, 180f, desiredWidth);
        Vector2 size = new(width, GetOverlayWindowHeight(buttonHeight));
        ImGui.SetNextWindowPos(new Vector2(display.X - size.X - margin, display.Y - size.Y - margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.88f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayMainEntry", flags))
        {
            if (ImGui.Button(ui.IslandEntry, new Vector2(-1f, buttonHeight)))
                OpenReplayManager();
        }
        ImGui.End();
        if (!_islandEntryLogged)
        {
            _islandEntryLogged = true;
            Logger.Info(
                LogTag,
                customLevelSelect
                    ? "Replay custom-level-page entry is available"
                    : "Replay main-page entry is available");
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
        float buttonHeight = GetOverlayButtonHeight();
        string primaryLabel = state == ReplayRunState.Paused ? ui.Resume : ui.Pause;
        float desiredWidth = state is ReplayRunState.Playing or ReplayRunState.Paused
            ? ImGui.CalcTextSize(primaryLabel).X
                + ImGui.CalcTextSize(ui.Stop).X
                + style.FramePadding.X * 4f
                + style.ItemSpacing.X
                + style.WindowPadding.X * 2f
            : ImGui.CalcTextSize(ui.Stop).X
                + style.FramePadding.X * 2f
                + style.WindowPadding.X * 2f;
        float width = ClampOverlayWidth(display.X, margin, 250f, desiredWidth);
        Vector2 size = new(width, GetOverlayWindowHeight(buttonHeight));
        ImGui.SetNextWindowPos(new Vector2(margin, display.Y - size.Y - margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayControls", flags))
        {
            if (state is ReplayRunState.Playing or ReplayRunState.Paused)
            {
                float buttonWidth = Math.Max(
                    70f,
                    (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
                if (ImGui.Button(primaryLabel, new Vector2(buttonWidth, buttonHeight)))
                    _commands.Enqueue(new ReplayCommand(ReplayCommandKind.TogglePause));
                ImGui.SameLine();
                if (ImGui.Button(ui.Stop, new Vector2(buttonWidth, buttonHeight)))
                    _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
            }
            else if (ImGui.Button(ui.Stop, new Vector2(-1f, buttonHeight)))
                _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
        }
        ImGui.End();
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
        string label = saved ? ui.ResultSaved : queued ? ui.SavingResult : ui.SaveResult;
        float desiredWidth = ImGui.CalcTextSize(label).X
            + style.FramePadding.X * 2f
            + style.WindowPadding.X * 2f;
        float width = ClampOverlayWidth(display.X, margin, 220f, desiredWidth);
        Vector2 size = new(width, GetOverlayWindowHeight(buttonHeight));
        ImGui.SetNextWindowPos(new Vector2(margin, display.Y - size.Y - margin), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayResultSave", flags))
        {
            if (ImGui.Button(label, new Vector2(-1f, buttonHeight)) && !saved && !queued)
            {
                lock (_stateLock)
                    _resultSaveQueued = true;
                SaveResultNow();
            }
        }
        ImGui.End();
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
        ImGui.SetNextWindowBgAlpha(0.94f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##ReplayToast", flags))
            ImGui.TextWrapped(toast);
        ImGui.End();
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

    private void DrawReplayManager()
    {
        if (!_managerOpen)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0f || display.Y <= 0f)
            return;

        ImGuiStylePtr baseStyle = ImGui.GetStyle();
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Vector2(Math.Max(baseStyle.WindowPadding.X, 18f), Math.Max(baseStyle.WindowPadding.Y, 14f)));
        ImGui.PushStyleVar(
            ImGuiStyleVar.FramePadding,
            new Vector2(Math.Max(baseStyle.FramePadding.X, 14f), Math.Max(baseStyle.FramePadding.Y, 9f)));
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(Math.Max(baseStyle.ItemSpacing.X, 12f), Math.Max(baseStyle.ItemSpacing.Y, 10f)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Math.Max(baseStyle.FrameRounding, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.97f);
        bool open = true;
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin($"{ui.ManagerTitle}###ReplayManager", ref open, flags))
        {
            ImGui.SetWindowFontScale(1.06f);
            List<ReplayFileEntry> files;
            lock (_stateLock)
                files = _files.ToList();
            ReplayFileEntry? selected = files.FirstOrDefault(entry =>
                string.Equals(entry.Path, _selectedReplayPath, StringComparison.Ordinal));
            if (_managerShowingDetails && selected != null)
            {
                DrawReplayDetailsPage(ui, selected, ref open);
            }
            else
            {
                _managerShowingDetails = false;
                DrawReplayListPage(ui, files, ref open);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(5);
        if (!open)
            CloseReplayManager();
    }

    private void DrawReplayListPage(UiText ui, List<ReplayFileEntry> files, ref bool open)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float buttonHeight = GetManagerButtonHeight();
        float availableWidth = ImGui.GetContentRegionAvail().X;
        bool toolbarOnOneLine = CanFitManagerButtonRow(
            availableWidth,
            ui.Refresh,
            ui.SaveCurrent,
            ui.Close);
        float toolbarButtonWidth = toolbarOnOneLine
            ? Math.Max(1f, (availableWidth - style.ItemSpacing.X * 2f) / 3f)
            : -1f;

        if (ImGui.Button(ui.Refresh, new Vector2(toolbarButtonWidth, buttonHeight)))
            RefreshFiles();
        if (toolbarOnOneLine)
            ImGui.SameLine();
        if (ImGui.Button(ui.SaveCurrent, new Vector2(toolbarButtonWidth, buttonHeight)))
            QueueSaveCurrent(ui);
        if (toolbarOnOneLine)
            ImGui.SameLine();
        if (ImGui.Button(ui.Close, new Vector2(toolbarButtonWidth, buttonHeight)))
            open = false;

        ShowNotice();
        ImGui.Separator();
        DrawManagerSectionTitle(ui.Search);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##replay-search", ref _fileSearch, 128);

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
                buttonHeight,
                ImGui.GetTextLineHeightWithSpacing() * 2f + style.FramePadding.Y * 2f);
            foreach (ReplayFileEntry entry in files)
            {
                string result = entry.Supported
                    ? entry.Completed ? ui.Complete : ui.Failed
                    : ui.Unsupported;
                int progress = GetEndProgress(entry);
                float titleWidth = Math.Max(
                    80f,
                    ImGui.GetContentRegionAvail().X - style.FramePadding.X * 2f);
                string title = EllipsizeManagerText(GetReplayDisplayTitle(entry), titleWidth);
                string metadata = $"{entry.RecordedAtUtc.ToLocalTime():MM-dd HH:mm}   {result}   {progress}%";
                string label = $"{title}\n{metadata}##replay-{entry.Path}";
                float rowWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
                if (!ImGui.Selectable(
                        label,
                        string.Equals(_selectedReplayPath, entry.Path, StringComparison.Ordinal),
                        ImGuiSelectableFlags.None,
                        new Vector2(rowWidth, rowHeight)))
                    continue;

                _selectedReplayPath = entry.Path;
                _pendingDeletePath = "";
                _deletePopupRequested = false;
                _editingReplayPath = entry.Path;
                _replayTitleEdit = entry.Title;
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
        if (ImGui.Button(ui.BackToList, new Vector2(buttonWidth, buttonHeight)))
        {
            _managerShowingDetails = false;
            _pendingDeletePath = "";
            _deletePopupRequested = false;
        }
        ImGui.SameLine();
        if (ImGui.Button(ui.Close, new Vector2(buttonWidth, buttonHeight)))
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
        DrawManagerSectionTitle(ui.Details);
        ImGui.TextWrapped(GetReplayDisplayTitle(selected));
        if (!string.IsNullOrWhiteSpace(selected.Title))
            DrawManagerMutedWrapped($"{ui.OriginalSong}: {CleanManagerText(selected.SongName)}");
        if (!string.IsNullOrWhiteSpace(selected.ArtistName))
            DrawManagerMutedWrapped($"{ui.Artist}: {CleanManagerText(selected.ArtistName)}");
        ImGui.TextDisabled($"{ui.RecordedAt}: {selected.RecordedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        int startProgress = GetProgress(selected.StartTile, selected.TotalTiles);
        int endProgress = GetEndProgress(selected);
        ImGui.TextDisabled($"{ui.Progress}: {startProgress}% - {endProgress}%");
        ImGui.ProgressBar(endProgress / 100f, new Vector2(-1f, 0f), $"{endProgress}%");
        ImGui.TextDisabled($"{ui.Inputs}: {selected.HitCount}");
        ImGui.TextDisabled($"{ui.Speed}: {selected.Speed:0.00}x");
        ImGui.TextDisabled($"{ui.Source}: {(selected.IsOfficialLevel ? ui.Official : ui.Custom)}");
        if (!selected.Supported)
        {
            ImGui.TextWrapped(selected.Error ?? ui.Unsupported);
        }
        else
        {
            if (!string.Equals(_editingReplayPath, selected.Path, StringComparison.Ordinal))
            {
                _editingReplayPath = selected.Path;
                _replayTitleEdit = selected.Title;
            }

            DrawManagerSectionTitle(ui.ReplayTitle);
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("##replay-title-edit", ref _replayTitleEdit, 128);
            bool titleChanged = !string.Equals(
                _replayTitleEdit.Trim(),
                selected.Title,
                StringComparison.Ordinal);
            ImGui.BeginDisabled(!titleChanged);
            if (ImGui.Button(
                    ui.SaveTitle + "##manager-save-title",
                    new Vector2(-1f, GetManagerButtonHeight())))
                SaveReplayTitle(selected, ui);
            ImGui.EndDisabled();
        }

        float actionButtonHeight = GetManagerButtonHeight();
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
        ImGui.TextColored(new Vector4(0.45f, 0.78f, 0.96f, 1f), text);
    }

    private static void DrawManagerMutedWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
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
        return builder.ToString().TrimEnd();
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
            ImGui.TextWrapped(notice);
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

    private void LoadSettings()
    {
        try
        {
            ReplaySettings settings = RequireStore().LoadSettings();
            bool currentSettings = settings.SettingsVersion >= 2;
            _saveFullClear = currentSettings ? settings.SaveFullClear : true;
            _saveEveryCompletion = currentSettings && settings.SaveEveryCompletion;
            _saveEveryFailure = currentSettings && settings.SaveEveryFailure;
            _saveFailureAt90Percent = !currentSettings || settings.SaveFailureAt90Percent;
            _ignoreAutoplay = settings.IgnoreAutoplay;
            _showReplayHud = settings.ShowReplayHud;
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
                SettingsVersion = 2,
                SaveFullClear = _saveFullClear,
                SaveEveryCompletion = _saveEveryCompletion,
                SaveEveryFailure = _saveEveryFailure,
                SaveFailureAt90Percent = _saveFailureAt90Percent,
                IgnoreAutoplay = _ignoreAutoplay,
                ShowReplayHud = _showReplayHud,
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
        if (game == null || controller == 0 || !game.IsGameWorld(controller)
            || game.GetControllerState(controller) != PlayerControlState)
            return;

        lock (_stateLock)
        {
            if (_recording || _activeReplay != null || _pendingReplay != null || _currentAttempt != null
                || _levelTransitionInProgress)
                return;
            if (_pendingAttemptController != 0 && _pendingAttemptController != controller)
            {
                _pendingAttemptController = controller;
                _pendingIdentity = null;
                _identityStableTicks = 0;
            }
        }

        if (!game.TryGetLevelIdentity(controller, out ReplayLevelIdentity? identity, out _)
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
        BeginAttempt(controller, startTile, identity);
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

    private void QueueAttemptStart(nint controller, int startTile)
    {
        // 起始砖在这里就地解析：此时 Start_Rewind 的原函数已经跑完，GCS.checkpointNum 与
        // 本局真实起点一致（PC 版同样在 Start_Rewind 的 postfix 里读取该字段）。
        int resolved = _game is { } game
            ? ResolveAttemptStartTile(game, controller, startTile)
            : Math.Max(0, startTile);
        lock (_stateLock)
        {
            if (_activeReplay != null || _pendingReplay != null)
                return;
            _currentAttempt = null;
            _recording = false;
            _pendingAttemptController = controller;
            _pendingAttemptStartTile = resolved;
            _pendingIdentity = null;
            _identityStableTicks = 0;
        }
    }

    private void SetResultAttempt(ReplayData replay, bool saved)
    {
        lock (_stateLock)
        {
            _resultAttempt = CloneReplay(replay);
            _resultAttemptSaved = saved;
            _resultSaveQueued = false;
        }
    }

    private void ClearResultAttemptLocked()
    {
        _resultAttempt = null;
        _resultAttemptSaved = false;
        _resultSaveQueued = false;
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
