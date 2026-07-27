using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace Replay.Mobile;

public sealed class ReplayPlugin : IModPlugin, IModSettings
{
    private const string LogTag = "Replay";
    private const int PlayerControlState = 4;
    private const int StableIdentityTicksRequired = 20;
    private const int ManagerPageSize = 12;

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
    private string _fileSearch = "";
    private string _pendingDeletePath = "";
    private bool _recording;
    private bool _levelTransitionInProgress;
    private bool _renderErrorLogged;
    private bool _managerOpen;
    private bool _managerPausedGame;
    private bool _resultAttemptSaved;
    private bool _resultSaveQueued;
    private bool _deletePopupRequested;
    private bool _islandEntryLogged;
    private long _lastSettingsGuiTick;
    private int _filePage;
    private string _notice = "";
    private DateTime _noticeUntilUtc;
    private string _toast = "";
    private DateTime _toastUntilUtc;

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
    public string Version => "1.4.2-mobile.13";
    public string Author => "Flower / ADOFAI.gg";
    public string Description => "Record and replay ADOFAI mobile runs with IL2CPP-native hooks";
    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    internal bool IsReplayActive
    {
        get
        {
            lock (_stateLock)
                return _activeReplay != null;
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
                || _loadStage is ReplayLoadStage.WaitingForTargetStart or ReplayLoadStage.LoadingCustomLevel;
            return replay != null
                && targetLevelStarting
                && _runState is not ReplayRunState.Finished and not ReplayRunState.Failed
                ? replay.StartTile
                : requestedSequence;
        }
    }

    internal void HandleStartRewind(nint controller, int sequenceId)
    {
        GameApi? game = _game;
        if (game == null)
            return;

        _controller = controller;
        _player = game.GetPlayer(controller);
        _languageCode = game.GetLanguageCode();
        _levelTransitionInProgress = false;

        lock (_stateLock)
        {
            if (_pendingReplay != null)
            {
                if (_loadStage == ReplayLoadStage.EnteringGameScene)
                {
                    _loadStage = ReplayLoadStage.ReadyToLoadCustomLevel;
                    Logger.Info(LogTag, "Game scene ready for custom replay");
                    return;
                }
                if (_loadStage == ReplayLoadStage.ReadyToLoadCustomLevel)
                    return;

                _activeReplay = _pendingReplay;
                _pendingReplay = null;
                _loadStage = ReplayLoadStage.None;
                _replayIndex = 0;
                _recording = false;
                _runState = ReplayRunState.WaitingForStart;
                game.SetPaused(controller, false);
                Logger.Info(LogTag, "Replay activated after level load");
                return;
            }

            if (_activeReplay != null && _runState is not ReplayRunState.Finished and not ReplayRunState.Failed)
            {
                _replayIndex = 0;
                _recording = false;
                _runState = ReplayRunState.WaitingForStart;
                game.SetPaused(controller, false);
                return;
            }

            if (_runState is ReplayRunState.Finished or ReplayRunState.Failed)
            {
                _activeReplay = null;
                _runState = ReplayRunState.Idle;
            }
        }

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
            }
        }

        ContinuePendingReplayLoad();
        EnsureAttemptStarted(controller);
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

    private void StartPlaybackNow(ReplayData replay)
    {
        GameApi? game = _game;
        if (game == null || replay.Hits.Count == 0)
            return;

        StopPlaybackNow(resumeRecording: false);
        ReplayData pendingReplay = CloneReplay(replay)!;
        bool needsGameSceneBridge = !pendingReplay.IsOfficialLevel
            && !game.CanHotLoadCustomReplayLevel()
            && !game.CanLoadScenes;
        lock (_stateLock)
        {
            ClearResultAttemptLocked();
            _activeReplay = null;
            _pendingReplay = pendingReplay;
            _currentAttempt = null;
            _runState = ReplayRunState.Loading;
            _loadStage = needsGameSceneBridge
                ? ReplayLoadStage.EnteringGameScene
                : ReplayLoadStage.WaitingForTargetStart;
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
            }
            string error = string.IsNullOrWhiteSpace(game.LastLoadError)
                ? UiText.FromLanguage(_languageCode).LoadFailed
                : game.LastLoadError;
            SetNotice(error);
            Logger.Error(LogTag, $"Could not load replay level: {replay.SongName}: {error}");
            return;
        }
        Logger.Info(LogTag, $"Replay load requested via {game.LastLoadRoute}: {game.GetReplayLoadState()}");
    }

    private void ContinuePendingReplayLoad()
    {
        GameApi? game = _game;
        ReplayData? replay;
        lock (_stateLock)
        {
            if (game == null
                || _pendingReplay == null
                || _loadStage != ReplayLoadStage.ReadyToLoadCustomLevel)
                return;
            replay = CloneReplay(_pendingReplay);
            _loadStage = ReplayLoadStage.LoadingCustomLevel;
        }

        if (replay != null && game.LoadReplayLevel(replay))
        {
            Logger.Info(LogTag, $"Replay load requested via {game.LastLoadRoute}: {game.GetReplayLoadState()}");
            return;
        }

        lock (_stateLock)
        {
            _activeReplay = null;
            _pendingReplay = null;
            _runState = ReplayRunState.Idle;
            _loadStage = ReplayLoadStage.None;
        }
        string error = string.IsNullOrWhiteSpace(game.LastLoadError)
            ? UiText.FromLanguage(_languageCode).LoadFailed
            : game.LastLoadError;
        SetNotice(error);
        Logger.Error(LogTag, $"Could not hot-load pending custom replay: {error}");
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
        GameApi? game = _game;
        nint controller = game?.GetController() ?? 0;
        bool pauseGame = game?.IsGameWorld(controller) == true && game.IsPaused(controller) == false;
        if (pauseGame)
            game?.SetPaused(controller, true);
        lock (_stateLock)
        {
            _managerOpen = true;
            _managerPausedGame = pauseGame;
        }
        RefreshFiles();
    }

    private void CloseReplayManager()
    {
        bool resume;
        lock (_stateLock)
        {
            resume = _managerPausedGame;
            _managerOpen = false;
            _managerPausedGame = false;
            _pendingDeletePath = "";
            _deletePopupRequested = false;
        }
        if (resume)
        {
            GameApi? game = _game;
            nint controller = game?.GetController() ?? 0;
            if (controller != 0)
                game?.SetPaused(controller, false);
        }
    }

    private void DrawIslandEntry()
    {
        GameApi? game = _game;
        bool levelSelect = game?.IsLevelSelect() == true;
        if (!levelSelect)
            _islandEntryLogged = false;
        long settingsAge = Environment.TickCount64 - Interlocked.Read(ref _lastSettingsGuiTick);
        ImGuiIOPtr io = ImGui.GetIO();
        if (!levelSelect || game == null || _managerOpen || settingsAge < 250 || io.WantTextInput)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        Vector2 display = io.DisplaySize;
        if (display.X < 200f || display.Y < 120f)
            return;
        Vector2 size = new(Math.Min(180f, display.X * 0.42f), 58f);
        ImGui.SetNextWindowPos(new Vector2(display.X - size.X - 18f, display.Y - size.Y - 24f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.88f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayMainEntry", flags))
        {
            if (ImGui.Button(ui.IslandEntry, new Vector2(-1f, 38f)))
                OpenReplayManager();
        }
        ImGui.End();
        if (!_islandEntryLogged)
        {
            _islandEntryLogged = true;
            Logger.Info(LogTag, "Replay main-page entry is available");
        }
    }

    private void DrawReplayControls()
    {
        if (_managerOpen || !_showReplayHud)
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
        Vector2 size = new(Math.Min(250f, display.X * 0.62f), 58f);
        ImGui.SetNextWindowPos(new Vector2(18f, display.Y - size.Y - 20f), ImGuiCond.Always);
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
                if (ImGui.Button(state == ReplayRunState.Paused ? ui.Resume : ui.Pause, new Vector2(buttonWidth, 38f)))
                    _commands.Enqueue(new ReplayCommand(ReplayCommandKind.TogglePause));
                ImGui.SameLine();
                if (ImGui.Button(ui.Stop, new Vector2(buttonWidth, 38f)))
                    _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
            }
            else if (ImGui.Button(ui.Stop, new Vector2(-1f, 38f)))
                _commands.Enqueue(new ReplayCommand(ReplayCommandKind.Stop));
        }
        ImGui.End();
    }

    private void DrawResultSaveButton()
    {
        if (_managerOpen)
            return;
        long settingsAge = Environment.TickCount64 - Interlocked.Read(ref _lastSettingsGuiTick);
        ImGuiIOPtr io = ImGui.GetIO();
        if (settingsAge < 250 || io.WantTextInput)
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
        Vector2 size = new(Math.Min(220f, display.X * 0.55f), 58f);
        ImGui.SetNextWindowPos(new Vector2(18f, display.Y - size.Y - 20f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("##ReplayResultSave", flags))
        {
            string label = saved ? ui.ResultSaved : queued ? ui.SavingResult : ui.SaveResult;
            if (ImGui.Button(label, new Vector2(-1f, 38f)) && !saved && !queued)
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
        Vector2 size = new(Math.Min(420f, display.X - 32f), 64f);
        ImGui.SetNextWindowPos(new Vector2((display.X - size.X) * 0.5f, 20f), ImGuiCond.Always);
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

    private void DrawReplayManager()
    {
        if (!_managerOpen)
            return;

        UiText ui = UiText.FromLanguage(_languageCode);
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0f || display.Y <= 0f)
            return;
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
            if (ImGui.Button(ui.Refresh))
                RefreshFiles();
            ImGui.SameLine();
            if (ImGui.Button(ui.SaveCurrent))
                QueueSaveCurrent(ui);
            ImGui.SameLine();
            if (ImGui.Button(ui.Close))
                open = false;

            ImGui.Separator();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText($"{ui.Search}##replay-search", ref _fileSearch, 128))
                _filePage = 0;

            List<ReplayFileEntry> files;
            lock (_stateLock)
                files = _files.ToList();
            string search = _fileSearch.Trim();
            if (!string.IsNullOrEmpty(search))
            {
                files = files.Where(entry =>
                        entry.SongName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || entry.ArtistName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            int pageCount = Math.Max(1, (files.Count + ManagerPageSize - 1) / ManagerPageSize);
            _filePage = Math.Clamp(_filePage, 0, pageCount - 1);
            ImGui.TextUnformatted($"{ui.Files}: {files.Count}    {ui.Page} {_filePage + 1}/{pageCount}");
            if (_filePage > 0 && ImGui.Button(ui.Previous))
                _filePage--;
            if (_filePage + 1 < pageCount)
            {
                if (_filePage > 0)
                    ImGui.SameLine();
                if (ImGui.Button(ui.Next))
                    _filePage++;
            }

            ImGui.Separator();
            if (files.Count == 0)
            {
                ImGui.TextDisabled(ui.NoFiles);
            }
            else
            {
                int start = _filePage * ManagerPageSize;
                int end = Math.Min(start + ManagerPageSize, files.Count);
                for (int index = start; index < end; index++)
                {
                    ReplayFileEntry entry = files[index];
                    string result = entry.Supported
                        ? entry.Completed ? ui.Complete : ui.Failed
                        : ui.Unsupported;
                    int progress = GetEndProgress(entry);
                    string label = $"{entry.RecordedAtUtc.ToLocalTime():MM-dd HH:mm}  [{result} {progress}%]  "
                        + $"{entry.SongName}##replay-{entry.Path}";
                    if (ImGui.Selectable(label, string.Equals(_selectedReplayPath, entry.Path, StringComparison.Ordinal)))
                    {
                        _selectedReplayPath = entry.Path;
                        _pendingDeletePath = "";
                    }
                }
            }

            ReplayFileEntry? selected = files.FirstOrDefault(entry =>
                string.Equals(entry.Path, _selectedReplayPath, StringComparison.Ordinal));
            if (selected != null)
            {
                ImGui.Separator();
                DrawReplayDetails(ui, selected);
            }
            ShowNotice();
        }
        ImGui.End();
        if (!open)
            CloseReplayManager();
    }

    private void DrawReplayDetails(UiText ui, ReplayFileEntry selected)
    {
        ImGui.TextUnformatted(ui.Details);
        ImGui.TextWrapped(selected.SongName);
        if (!string.IsNullOrWhiteSpace(selected.ArtistName))
            ImGui.TextWrapped($"{ui.Artist}: {selected.ArtistName}");
        ImGui.TextUnformatted($"{ui.RecordedAt}: {selected.RecordedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        int startProgress = GetProgress(selected.StartTile, selected.TotalTiles);
        int endProgress = GetEndProgress(selected);
        ImGui.TextUnformatted($"{ui.Progress}: {startProgress}% - {endProgress}%");
        ImGui.ProgressBar(endProgress / 100f, new Vector2(-1f, 0f), $"{endProgress}%");
        ImGui.TextUnformatted($"{ui.Inputs}: {selected.HitCount}");
        ImGui.TextUnformatted($"{ui.Speed}: {selected.Speed:0.00}x");
        ImGui.TextUnformatted($"{ui.Source}: {(selected.IsOfficialLevel ? ui.Official : ui.Custom)}");
        if (!selected.Supported)
        {
            ImGui.TextWrapped(selected.Error ?? ui.Unsupported);
        }
        else if (ImGui.Button(ui.Play + "##manager-play"))
        {
            QueueReplayFile(selected.Path, ui);
        }
        if (selected.Supported)
            ImGui.SameLine();
        if (ImGui.Button(ui.Delete + "##manager-delete"))
        {
            _pendingDeletePath = selected.Path;
            _deletePopupRequested = true;
        }

        if (_deletePopupRequested && string.Equals(_pendingDeletePath, selected.Path, StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextWrapped(ui.ConfirmDelete);
            if (ImGui.Button(ui.Delete + "##manager-confirm-delete"))
            {
                DeleteReplayFile(selected.Path);
                _deletePopupRequested = false;
            }
            ImGui.SameLine();
            if (ImGui.Button(ui.Cancel + "##manager-cancel-delete"))
            {
                _pendingDeletePath = "";
                _deletePopupRequested = false;
            }
        }
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

    private void DeleteReplayFile(string path)
    {
        try
        {
            File.Delete(path);
            _selectedReplayPath = "";
            _pendingDeletePath = "";
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
        string song = includeSong && data != null ? $"  {data.SongName}" : "";
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
            startTile = _pendingAttemptStartTile >= 0
                ? _pendingAttemptStartTile
                : game.GetCurrentSequence(controller);
            _pendingAttemptController = 0;
            _pendingAttemptStartTile = -1;
            _pendingIdentity = null;
            _identityStableTicks = 0;
        }
        BeginAttempt(controller, startTile, identity);
    }

    private void QueueAttemptStart(nint controller, int startTile)
    {
        lock (_stateLock)
        {
            if (_activeReplay != null || _pendingReplay != null)
                return;
            _currentAttempt = null;
            _recording = false;
            _pendingAttemptController = controller;
            _pendingAttemptStartTile = Math.Max(0, startTile);
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
