using System.Runtime.InteropServices;
using System.Text.Json;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.RuntimeAbstractions;

namespace Replay.Mobile;

internal sealed class GameApi
{
    private readonly IAppDomain _domain;
    private readonly IRuntimeAssembly _gameAssembly;
    private readonly IRuntimeClass _controllerClass;
    private readonly IRuntimeClass _playerClass;
    private readonly IRuntimeClass _planetClass;
    private readonly IRuntimeClass _floorClass;
    private readonly IRuntimeClass _conductorClass;
    private readonly IRuntimeClass _planetarySystemClass;
    private readonly IRuntimeClass? _levelMakerClass;
    private readonly IRuntimeClass? _failBarClass;
    private readonly IRuntimeClass? _adoBaseClass;
    private readonly IRuntimeClass? _gameClass;
    private readonly IRuntimeClass? _levelDataClass;
    private readonly IRuntimeClass? _levelSelectClass;
    private readonly IRuntimeClass? _gcsClass;
    private readonly IRuntimeClass? _rdStringClass;

    private readonly IRuntimeField? _controllerInstance;
    private readonly IRuntimeField? _controllerGameWorld;
    private readonly IRuntimeField? _controllerCurrentState;
    private readonly IRuntimeField? _controllerCurrentSequence;
    private readonly IRuntimeField? _controllerSetupComplete;
    private readonly IRuntimeField? _controllerTransitioningLevel;
    private readonly IRuntimeField? _controllerNoFailInfinite;
    private readonly IRuntimeField? _controllerPaused;
    private readonly IRuntimeField? _controllerLevelName;
    private readonly IRuntimeField? _controllerMultipressPenalty;
    private readonly IRuntimeField? _controllerMultipressFirst;

    private readonly IRuntimeField? _playerPlanetarySystem;
    private readonly IRuntimeField? _playerMidspinInfinite;
    private readonly IRuntimeField? _playerConsecutiveMultipress;
    private readonly IRuntimeField? _playerKeyTimes;
    private readonly IRuntimeField? _playerFailBar;
    private readonly IRuntimeField? _systemChosenPlanet;
    private readonly IRuntimeField? _systemClockwise;
    private readonly IRuntimeField? _systemSpeed;
    private readonly IRuntimeField? _failBarMultipressCounter;
    private readonly IRuntimeField? _failBarOverloadCounter;

    private readonly IRuntimeField? _planetAngle;
    private readonly IRuntimeField? _planetCachedAngle;
    private readonly IRuntimeField? _planetTargetExitAngle;
    private readonly IRuntimeField? _planetCurrentFloor;

    private readonly IRuntimeField? _floorSequence;
    private readonly IRuntimeField? _floorMidSpin;
    private readonly IRuntimeField? _floorNext;
    private readonly IRuntimeField? _floorAuto;
    private readonly IRuntimeField? _floorMarginScale;
    private readonly IRuntimeField? _floorLevelNumber;
    private readonly IRuntimeField? _floorIsPortal;
    private readonly IRuntimeField? _floorRenderer;

    private readonly IRuntimeField? _conductorBpm;
    private readonly IRuntimeField? _conductorSong;
    private readonly IRuntimeField? _conductorInstance;

    private readonly IRuntimeField? _gameInstance;
    private readonly IRuntimeField? _gameLevelPath;
    private readonly IRuntimeField? _gameLevelData;
    private readonly IRuntimeField? _gameIsLoading;
    private readonly IRuntimeField? _levelArtist;
    private readonly IRuntimeField? _levelSelectRdFloor;
    private readonly IRuntimeField? _levelMakerInstance;
    private readonly IRuntimeField? _levelMakerFloors;

    private readonly IRuntimeField? _checkpoint;
    private readonly IRuntimeField? _sceneToLoad;
    private readonly IRuntimeField? _internalLevelName;
    private readonly IRuntimeField? _customLevelPaths;
    private readonly IRuntimeField? _loadCustomFromBundle;
    private readonly IRuntimeField? _customLevelIndex;
    private readonly IRuntimeField? _customLevelId;
    private readonly IRuntimeField? _currentSpeedTrial;
    private readonly IRuntimeField? _nextSpeedRun;
    private readonly IRuntimeField? _lofiVersion;
    private readonly IRuntimeField? _language;
    private readonly IRuntimeField? _textValue;

    private readonly IRuntimeMethod? _getController;
    private readonly IRuntimeMethod? _getPlayerOne;
    private readonly IRuntimeMethod? _getConductor;
    private readonly IRuntimeMethod? _getConductorInstance;
    private readonly IRuntimeMethod? _getIsOfficial;
    private readonly IRuntimeMethod? _getCurrentLevel;
    private readonly IRuntimeMethod? _getSceneName;
    private readonly IRuntimeMethod? _getIsLevelSelect;
    private readonly IRuntimeMethod? _getLevelSelect;
    private readonly IRuntimeMethod? _getLevelSelectBase;
    private readonly IRuntimeMethod? _getLevelMaker;
    private readonly IRuntimeMethod? _getPercentComplete;
    private readonly IRuntimeMethod? _getPlayerAuto;
    private readonly IRuntimeMethod? _getLevelArtist;
    private readonly IRuntimeMethod? _getLevelSong;

    private readonly RestartDelegate? _restart;
    private readonly nint _restartMethodInfo;
    private readonly SetBooleanDelegate? _setPaused;
    private readonly nint _setPausedMethodInfo;
    private readonly SetBooleanDelegate? _setAudioPaused;
    private readonly nint _setAudioPausedMethodInfo;
    private readonly EnterLevelDelegate? _enterLevel;
    private readonly nint _enterLevelMethodInfo;
    private readonly LoadSceneDelegate? _loadScene;
    private readonly nint _loadSceneMethodInfo;
    private readonly LoadCustomLevelDelegate? _loadCustomLevel;
    private readonly nint _loadCustomLevelMethodInfo;
    private readonly LoadAndPlayLevelDelegate? _loadAndPlayLevel;
    private readonly nint _loadAndPlayLevelMethodInfo;
    private readonly FindObjectDelegate? _findGameObject;
    private readonly nint _findGameObjectMethodInfo;
    private readonly InstantiateWithParentDelegate? _instantiateWithParent;
    private readonly nint _instantiateWithParentMethodInfo;
    private readonly GetObjectDelegate? _getComponentTransform;
    private readonly nint _getComponentTransformMethodInfo;
    private readonly GetObjectDelegate? _getGameObjectTransform;
    private readonly nint _getGameObjectTransformMethodInfo;
    private readonly GetObjectDelegate? _getComponentGameObject;
    private readonly nint _getComponentGameObjectMethodInfo;
    private readonly GetObjectDelegate? _getTransformParent;
    private readonly nint _getTransformParentMethodInfo;
    private readonly SetVector3Delegate? _setTransformPosition;
    private readonly nint _setTransformPositionMethodInfo;
    private readonly SetStringDelegate? _setObjectName;
    private readonly nint _setObjectNameMethodInfo;
    private readonly SetBooleanDelegate? _setGameObjectActive;
    private readonly nint _setGameObjectActiveMethodInfo;
    private readonly GetComponentByNameDelegate? _getComponentByName;
    private readonly nint _getComponentByNameMethodInfo;
    private readonly SetBooleanDelegate? _setBehaviourEnabled;
    private readonly nint _setBehaviourEnabledMethodInfo;
    private readonly DestroyObjectDelegate? _destroyObject;
    private readonly nint _destroyObjectMethodInfo;
    private readonly SetStringDelegate? _setText;
    private readonly nint _setTextMethodInfo;

    private nint _replayIslandFloorObject;
    private nint _replayIslandLabelObject;

    internal string LastLoadError { get; private set; } = "";
    internal string LastLoadRoute { get; private set; } = "";
    internal bool CanLoadScenes => _loadCustomLevel != null;
    internal bool CanCreateIslandFloor => CanCreateIslandEntry();
    internal string LastIslandEntryError { get; private set; } = "";

    private GameApi(IAppDomain domain, IRuntimeAssembly gameAssembly)
    {
        _domain = domain;
        _gameAssembly = gameAssembly;
        _controllerClass = RequireClass("", "scrController");
        _playerClass = RequireClass("", "scrPlayer");
        _planetClass = RequireClass("", "scrPlanet");
        _floorClass = RequireClass("", "scrFloor");
        _conductorClass = RequireClass("", "scrConductor");
        _planetarySystemClass = RequireClass("", "PlanetarySystem");
        _levelMakerClass = FindClass("", "scrLevelMaker");
        _failBarClass = FindClass("", "scrFailBar");
        _adoBaseClass = FindClass("", "ADOBase");
        _gameClass = FindClass("", "scnGame");
        _levelDataClass = FindClass("ADOFAI", "LevelData");
        _levelSelectClass = FindClass("", "scnLevelSelect");
        _gcsClass = FindClass("", "GCS");
        _rdStringClass = FindClass("", "RDString");

        _controllerInstance = FindField(_controllerClass, "_instance", "instance");
        _controllerGameWorld = FindField(_controllerClass, "gameworld", "isGameWorld", "isgameworld");
        _controllerCurrentState = FindField(_controllerClass, "currentState");
        _controllerCurrentSequence = FindField(_controllerClass, "currentSeqID", "currentSequenceId");
        _controllerSetupComplete = FindField(_controllerClass, "setupComplete");
        _controllerTransitioningLevel = FindField(_controllerClass, "transitioningLevel");
        _controllerNoFailInfinite = FindField(_controllerClass, "noFailInfiniteMargin");
        _controllerPaused = FindField(_controllerClass, "_paused", "paused");
        _controllerLevelName = FindField(_controllerClass, "levelName", "originalLevelName");
        _controllerMultipressPenalty = FindField(_controllerClass, "multipressPenalty");
        _controllerMultipressFirst = FindField(_controllerClass, "multipressAndHasPressedFirstPress");

        _playerPlanetarySystem = FindField(_playerClass, "planetarySystem");
        _playerMidspinInfinite = FindField(_playerClass, "midspinInfiniteMargin");
        _playerConsecutiveMultipress = FindField(_playerClass, "consecMultipressCounter");
        _playerKeyTimes = FindField(_playerClass, "keyTimes");
        _playerFailBar = FindField(_playerClass, "failBar");
        _systemChosenPlanet = FindField(_planetarySystemClass, "chosenPlanet", "chosenplanet");
        _systemClockwise = FindField(_planetarySystemClass, "isCW", "clockwise");
        _systemSpeed = FindField(_planetarySystemClass, "speed");
        _failBarMultipressCounter = FindField(_failBarClass, "multipressCounter");
        _failBarOverloadCounter = FindField(_failBarClass, "overloadCounter");

        _planetAngle = FindField(_planetClass, "angle");
        _planetCachedAngle = FindField(_planetClass, "cachedAngle");
        _planetTargetExitAngle = FindField(_planetClass, "<targetExitAngle>k__BackingField", "targetExitAngle");
        _planetCurrentFloor = FindField(_planetClass, "currfloor", "currFloor");

        _floorSequence = FindField(_floorClass, "seqID", "sequenceId");
        _floorMidSpin = FindField(_floorClass, "midSpin");
        _floorNext = FindField(_floorClass, "nextfloor", "nextFloor");
        _floorAuto = FindField(_floorClass, "auto");
        _floorMarginScale = FindField(_floorClass, "marginScale");
        _floorLevelNumber = FindField(_floorClass, "levelnumber");
        _floorIsPortal = FindField(_floorClass, "isportal");
        _floorRenderer = FindField(_floorClass, "floorRenderer");

        _conductorBpm = FindField(_conductorClass, "bpm");
        _conductorSong = FindField(_conductorClass, "song");
        _conductorInstance = FindField(_conductorClass, "_instance", "instance");

        _gameInstance = FindField(_gameClass, "instance", "_instance");
        _gameLevelPath = FindField(_gameClass, "levelPath");
        _gameLevelData = FindField(_gameClass, "levelData");
        _gameIsLoading = FindField(_gameClass, "isLoading");
        _levelArtist = FindField(_levelDataClass, "artist");
        _levelSelectRdFloor = FindField(_levelSelectClass, "rdFloor");
        _levelMakerInstance = FindField(_levelMakerClass, "_instance", "instance");
        _levelMakerFloors = FindField(_levelMakerClass, "listFloors");

        _checkpoint = FindField(_gcsClass, "_checkpointNum", "checkpointNum");
        _sceneToLoad = FindField(_gcsClass, "sceneToLoad");
        _internalLevelName = FindField(_gcsClass, "internalLevelName");
        _customLevelPaths = FindField(_gcsClass, "customLevelPaths");
        _loadCustomFromBundle = FindField(_gcsClass, "loadCustomFromBundle");
        _customLevelIndex = FindField(_gcsClass, "customLevelIndex");
        _customLevelId = FindField(_gcsClass, "customLevelId");
        _currentSpeedTrial = FindField(_gcsClass, "currentSpeedTrial");
        _nextSpeedRun = FindField(_gcsClass, "nextSpeedRun");
        _lofiVersion = FindField(_gcsClass, "lofiVersion");
        _language = FindField(_rdStringClass, "language");

        _getController = _adoBaseClass?.GetMethod("get_controller", 0)
            ?? _controllerClass.GetMethod("get_instance", 0);
        _getPlayerOne = _controllerClass.GetMethod("get_playerOne", 0);
        _getConductor = _adoBaseClass?.GetMethod("get_conductor", 0);
        _getConductorInstance = _conductorClass.GetMethod("get_instance", 0);
        _getIsOfficial = _adoBaseClass?.GetMethod("get_isOfficialLevel", 0);
        _getCurrentLevel = _adoBaseClass?.GetMethod("get_currentLevel", 0);
        _getSceneName = _adoBaseClass?.GetMethod("get_sceneName", 0);
        _getIsLevelSelect = _adoBaseClass?.GetMethod("get_isLevelSelect", 0);
        _getLevelSelect = _adoBaseClass?.GetMethod("get_levelSelect", 0);
        _getLevelSelectBase = _adoBaseClass?.GetMethod("get_levelSelectBase", 0);
        _getLevelMaker = _adoBaseClass?.GetMethod("get_lm", 0)
            ?? _levelMakerClass?.GetMethod("get_instance", 0);
        _getPercentComplete = _controllerClass.GetMethod("get_percentComplete", 0);
        _getPlayerAuto = _playerClass.GetMethod("get_auto", 0);
        _getLevelArtist = _levelDataClass?.GetMethod("get_artist", 0);
        _getLevelSong = _levelDataClass?.GetMethod("get_song", 0);

        IRuntimeMethod? restartMethod = _controllerClass.GetMethod("Restart", 1);
        nint restartPointer = restartMethod?.FunctionPtr ?? 0;
        if (restartMethod != null && restartPointer != 0)
        {
            _restart = Marshal.GetDelegateForFunctionPointer<RestartDelegate>(restartPointer);
            _restartMethodInfo = restartMethod.Ptr;
        }

        IRuntimeMethod? setPausedMethod = _controllerClass.GetMethod("set_paused", 1);
        nint setPausedPointer = setPausedMethod?.FunctionPtr ?? 0;
        if (setPausedMethod != null && setPausedPointer != 0)
        {
            _setPaused = Marshal.GetDelegateForFunctionPointer<SetBooleanDelegate>(setPausedPointer);
            _setPausedMethodInfo = setPausedMethod.Ptr;
        }

        IRuntimeMethod? setAudioPausedMethod = _controllerClass.GetMethod("set_audioPaused", 1);
        nint setAudioPausedPointer = setAudioPausedMethod?.FunctionPtr ?? 0;
        if (setAudioPausedMethod != null && setAudioPausedPointer != 0)
        {
            _setAudioPaused = Marshal.GetDelegateForFunctionPointer<SetBooleanDelegate>(setAudioPausedPointer);
            _setAudioPausedMethodInfo = setAudioPausedMethod.Ptr;
        }

        IRuntimeMethod? enterLevelMethod = _controllerClass.GetMethod("EnterLevel", 2);
        nint enterLevelPointer = enterLevelMethod?.FunctionPtr ?? 0;
        if (enterLevelMethod != null && enterLevelPointer != 0)
        {
            _enterLevel = Marshal.GetDelegateForFunctionPointer<EnterLevelDelegate>(enterLevelPointer);
            _enterLevelMethodInfo = enterLevelMethod.Ptr;
        }

        IRuntimeClass? sceneManagerClass = FindClassInDomain(
            "UnityEngine.SceneManagement",
            "SceneManager");
        IRuntimeMethod? loadSceneMethod = sceneManagerClass?.GetMethod(
            "LoadScene",
            new[] { "System.String" })
            ?? sceneManagerClass?.GetMethod("LoadScene", new[] { "String" });
        nint loadScenePointer = loadSceneMethod?.FunctionPtr ?? 0;
        if (loadSceneMethod != null && loadScenePointer != 0)
        {
            _loadScene = Marshal.GetDelegateForFunctionPointer<LoadSceneDelegate>(loadScenePointer);
            _loadSceneMethodInfo = loadSceneMethod.Ptr;
        }

        IRuntimeMethod? loadCustomLevelMethod = _controllerClass.GetMethod("LoadCustomLevel", 3);
        nint loadCustomLevelPointer = loadCustomLevelMethod?.FunctionPtr ?? 0;
        if (loadCustomLevelMethod != null && loadCustomLevelPointer != 0)
        {
            _loadCustomLevel = Marshal.GetDelegateForFunctionPointer<LoadCustomLevelDelegate>(loadCustomLevelPointer);
            _loadCustomLevelMethodInfo = loadCustomLevelMethod.Ptr;
        }

        IRuntimeMethod? loadAndPlayLevelMethod = _gameClass?.GetMethod("LoadAndPlayLevel", 1);
        nint loadAndPlayLevelPointer = loadAndPlayLevelMethod?.FunctionPtr ?? 0;
        if (loadAndPlayLevelMethod != null && loadAndPlayLevelPointer != 0)
        {
            _loadAndPlayLevel = Marshal.GetDelegateForFunctionPointer<LoadAndPlayLevelDelegate>(loadAndPlayLevelPointer);
            _loadAndPlayLevelMethodInfo = loadAndPlayLevelMethod.Ptr;
        }

        IRuntimeClass? objectClass = FindClassInDomain("UnityEngine", "Object");
        IRuntimeClass? gameObjectClass = FindClassInDomain("UnityEngine", "GameObject");
        IRuntimeClass? componentClass = FindClassInDomain("UnityEngine", "Component");
        IRuntimeClass? transformClass = FindClassInDomain("UnityEngine", "Transform");
        IRuntimeClass? behaviourClass = FindClassInDomain("UnityEngine", "Behaviour");
        IRuntimeClass? textClass = FindClassInDomain("UnityEngine.UI", "Text");
        _textValue = FindField(textClass, "m_Text");

        _findGameObject = Bind<FindObjectDelegate>(gameObjectClass, "Find", new[] { "System.String" }, out _findGameObjectMethodInfo);
        _instantiateWithParent = Bind<InstantiateWithParentDelegate>(objectClass, "Instantiate",
            new[] { "UnityEngine.Object", "UnityEngine.Transform" }, out _instantiateWithParentMethodInfo);
        _getComponentTransform = Bind<GetObjectDelegate>(componentClass, "get_transform", Array.Empty<string>(), out _getComponentTransformMethodInfo);
        _getGameObjectTransform = Bind<GetObjectDelegate>(gameObjectClass, "get_transform", Array.Empty<string>(), out _getGameObjectTransformMethodInfo);
        _getComponentGameObject = Bind<GetObjectDelegate>(componentClass, "get_gameObject", Array.Empty<string>(), out _getComponentGameObjectMethodInfo);
        _getTransformParent = Bind<GetObjectDelegate>(transformClass, "get_parent", Array.Empty<string>(), out _getTransformParentMethodInfo);
        _setTransformPosition = Bind<SetVector3Delegate>(transformClass, "set_position",
            new[] { "UnityEngine.Vector3" }, out _setTransformPositionMethodInfo);
        _setObjectName = Bind<SetStringDelegate>(objectClass, "set_name", new[] { "System.String" }, out _setObjectNameMethodInfo);
        _setGameObjectActive = Bind<SetBooleanDelegate>(gameObjectClass, "SetActive", new[] { "System.Boolean" }, out _setGameObjectActiveMethodInfo);
        _getComponentByName = Bind<GetComponentByNameDelegate>(gameObjectClass, "GetComponent",
            new[] { "System.String" }, out _getComponentByNameMethodInfo);
        _setBehaviourEnabled = Bind<SetBooleanDelegate>(behaviourClass, "set_enabled",
            new[] { "System.Boolean" }, out _setBehaviourEnabledMethodInfo);
        _destroyObject = Bind<DestroyObjectDelegate>(objectClass, "Destroy",
            new[] { "UnityEngine.Object" }, out _destroyObjectMethodInfo);
        _setText = Bind<SetStringDelegate>(textClass, "set_text", new[] { "System.String" }, out _setTextMethodInfo);
    }

    internal static GameApi? Create()
    {
        IAppDomain? domain = RuntimeManager.GetDomain();
        if (domain == null)
            return null;
        IRuntimeAssembly? game = domain.OpenAssembly("Assembly-CSharp.dll")
            ?? domain.OpenAssembly("Assembly-CSharp");
        return game == null ? null : new GameApi(domain, game);
    }

    internal IRuntimeMethod? ResolveMethod(string className, string methodName, int parameterCount)
    {
        return FindClass("", className)?.GetMethod(methodName, parameterCount);
    }

    internal nint GetController()
    {
        nint controller = InvokeStaticObject(_getController);
        return controller != 0 ? controller : Read(_controllerInstance, 0, nint.Zero);
    }

    internal nint GetPlayer(nint controller)
    {
        if (controller == 0)
            return 0;
        try
        {
            return _getPlayerOne?.Invoke(controller) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    internal nint GetConductor()
    {
        nint conductor = InvokeStaticObject(_getConductor);
        return conductor != 0 ? conductor : Read(_conductorInstance, 0, nint.Zero);
    }

    internal nint GetChosenPlanet(nint player)
    {
        nint system = Read(_playerPlanetarySystem, player, nint.Zero);
        return Read(_systemChosenPlanet, system, nint.Zero);
    }

    internal nint GetCurrentFloor(nint player)
    {
        return Read(_planetCurrentFloor, GetChosenPlanet(player), nint.Zero);
    }

    internal bool IsGameWorld(nint controller)
    {
        return controller != 0 && Read(_controllerGameWorld, controller, (byte)0) != 0;
    }

    internal int GetControllerState(nint controller)
    {
        return Read(_controllerCurrentState, controller, 0);
    }

    internal int GetCurrentSequence(nint controller)
    {
        return Read(_controllerCurrentSequence, controller, 0);
    }

    internal bool IsLevelTransitioning(nint controller)
    {
        return controller != 0 && Read(_controllerTransitioningLevel, controller, (byte)0) != 0;
    }

    internal bool IsLevelSelect()
    {
        string sceneName = InvokeStaticString(_getSceneName);
        if (!string.IsNullOrWhiteSpace(sceneName))
        {
            return string.Equals(sceneName, "scnLevelSelect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneName, "scnLevelSelectBase", StringComparison.OrdinalIgnoreCase);
        }
        nint controller = GetController();
        if (IsGameWorld(controller))
            return false;
        try
        {
            if (_getIsLevelSelect?.InvokeStaticUnbox<byte>() != 0)
                return true;
        }
        catch
        {
        }
        return InvokeStaticObject(_getLevelSelect) != 0 || InvokeStaticObject(_getLevelSelectBase) != 0;
    }

    internal bool CanHotLoadCustomReplayLevel()
    {
        nint controller = GetController();
        return _loadAndPlayLevel != null
            && IsGameWorld(controller)
            && Read(_gameInstance, 0, nint.Zero) != 0;
    }

    internal bool EnsureReplayIslandEntry(int portalId, string label)
    {
        LastIslandEntryError = "";
        if (_replayIslandFloorObject != 0)
            return true;
        if (!CanCreateIslandEntry())
            return FailIslandEntry("Unity object APIs are incomplete");

        try
        {
            nint existing = FindGameObject("FloorReplay");
            if (existing != 0)
            {
                _replayIslandFloorObject = existing;
                return true;
            }

            nint originalFloorObject = FindGameObject("FloorCalibration");
            nint originalFloor = GetComponent(originalFloorObject, "scrFloor");
            bool cloneGameObject = originalFloorObject != 0 && originalFloor != 0;
            if (originalFloor == 0)
            {
                nint levelSelect = InvokeStaticObject(_getLevelSelect);
                originalFloor = Read(_levelSelectRdFloor, levelSelect, nint.Zero);
                originalFloorObject = GetGameObject(originalFloor);
            }
            if (originalFloor == 0 || originalFloorObject == 0)
                return FailIslandEntry("calibration floor was not found");

            nint parentObject = FindGameObject("outer ring");
            nint parent = GetGameObjectTransform(parentObject);
            if (parent == 0)
            {
                nint originalTransform = GetComponentTransform(originalFloor);
                parent = _getTransformParent?.Invoke(originalTransform, _getTransformParentMethodInfo) ?? 0;
            }
            if (parent == 0)
                return FailIslandEntry("floor parent was not found");

            nint clonedObject = _instantiateWithParent?.Invoke(
                cloneGameObject ? originalFloorObject : originalFloor,
                parent,
                _instantiateWithParentMethodInfo) ?? 0;
            if (clonedObject == 0)
                return FailIslandEntry("calibration floor could not be cloned");
            nint replayFloorObject = cloneGameObject ? clonedObject : GetGameObject(clonedObject);
            nint replayFloor = cloneGameObject ? GetComponent(replayFloorObject, "scrFloor") : clonedObject;
            if (replayFloorObject == 0)
                return FailIslandEntry("cloned floor GameObject was not found");
            if (replayFloor == 0)
                return FailIslandEntry("cloned scrFloor component was not found");

            SetObjectName(replayFloorObject, "FloorReplay");
            SetPosition(GetComponentTransform(replayFloor), new NativeVector3(2f, 3f, 0f));
            Write(_floorLevelNumber, replayFloor, portalId);
            Write(_floorIsPortal, replayFloor, (byte)1);
            nint renderer = Read(_floorRenderer, replayFloor, nint.Zero);
            if (renderer != 0)
                _setBehaviourEnabled?.Invoke(renderer, 1, _setBehaviourEnabledMethodInfo);
            _setGameObjectActive?.Invoke(replayFloorObject, 1, _setGameObjectActiveMethodInfo);
            _replayIslandFloorObject = replayFloorObject;

            TryCreateReplayIslandLabel(label);
            return true;
        }
        catch (Exception exception)
        {
            return FailIslandEntry(exception.Message);
        }
    }

    internal void ResetReplayIslandEntryReference()
    {
        _replayIslandFloorObject = 0;
        _replayIslandLabelObject = 0;
        LastIslandEntryError = "";
    }

    internal void RemoveReplayIslandEntry()
    {
        if (IsLevelSelect())
        {
            if (_replayIslandFloorObject != 0)
                _destroyObject?.Invoke(_replayIslandFloorObject, _destroyObjectMethodInfo);
            if (_replayIslandLabelObject != 0)
                _destroyObject?.Invoke(_replayIslandLabelObject, _destroyObjectMethodInfo);
        }
        ResetReplayIslandEntryReference();
    }

    internal float GetPercentComplete(nint controller)
    {
        if (controller == 0)
            return 0f;
        try
        {
            if (_getPercentComplete != null)
                return Math.Clamp(_getPercentComplete.InvokeUnbox<float>(controller), 0f, 1f);
        }
        catch
        {
        }
        int totalTiles = GetTotalTiles();
        return totalTiles <= 0
            ? 0f
            : Math.Clamp((GetCurrentSequence(controller) + 1f) / totalTiles, 0f, 1f);
    }

    internal int GetTotalTiles()
    {
        nint levelMaker = InvokeStaticObject(_getLevelMaker);
        if (levelMaker == 0)
            levelMaker = Read(_levelMakerInstance, 0, nint.Zero);
        nint floors = Read(_levelMakerFloors, levelMaker, nint.Zero);
        if (floors == 0)
            return 0;
        try
        {
            return Math.Max(0, new RuntimeObject(floors).InvokeUnbox<int>("get_Count", 0));
        }
        catch
        {
            return 0;
        }
    }

    internal bool TryGetLevelIdentity(
        nint controller,
        out ReplayLevelIdentity? identity,
        out string reason)
    {
        identity = null;
        reason = "";
        if (!IsGameWorld(controller))
            return false;
        if (Read(_controllerSetupComplete, controller, (byte)1) == 0)
        {
            reason = "controller setup is incomplete";
            return false;
        }
        if (IsLevelTransitioning(controller))
        {
            reason = "controller is transitioning";
            return false;
        }

        int totalTiles = GetTotalTiles();
        if (totalTiles <= 1)
        {
            reason = "floor list is not ready";
            return false;
        }

        bool official = IsOfficialLevel();
        string songName = GetSongName(controller).Trim();
        if (official)
        {
            string levelId = GetLevelId();
            if (string.IsNullOrWhiteSpace(levelId))
            {
                reason = "official level ID is not ready";
                return false;
            }
            identity = new ReplayLevelIdentity(
                string.IsNullOrWhiteSpace(songName) ? levelId : songName,
                "ADOFAI",
                "",
                GetSceneName(),
                levelId,
                true,
                totalTiles);
            return true;
        }

        nint game = Read(_gameInstance, 0, nint.Zero);
        nint levelData = Read(_gameLevelData, game, nint.Zero);
        string levelPath = ReadString(_gameLevelPath, game);
        if (game == 0 || levelData == 0 || Read(_gameIsLoading, game, (byte)0) != 0)
        {
            reason = "custom level data is not ready";
            return false;
        }
        if (string.IsNullOrWhiteSpace(levelPath) || !File.Exists(levelPath))
        {
            reason = "custom level path is not ready";
            return false;
        }
        if (string.IsNullOrWhiteSpace(songName))
        {
            reason = "custom song name is not ready";
            return false;
        }

        identity = new ReplayLevelIdentity(
            songName,
            GetArtistName(),
            levelPath,
            "",
            "",
            false,
            totalTiles);
        return true;
    }

    internal int GetFloorSequence(nint floor)
    {
        return Read(_floorSequence, floor, 0);
    }

    internal bool IsPaused(nint controller)
    {
        return Read(_controllerPaused, controller, (byte)0) != 0;
    }

    internal void SetPaused(nint controller, bool paused)
    {
        if (controller == 0)
            return;
        byte value = (byte)(paused ? 1 : 0);
        if (_setPaused != null)
            _setPaused(controller, value, _setPausedMethodInfo);
        else
            Write(_controllerPaused, controller, value);
        _setAudioPaused?.Invoke(controller, value, _setAudioPausedMethodInfo);
    }

    internal bool IsMidSpin(nint floor)
    {
        return Read(_floorMidSpin, floor, (byte)0) != 0;
    }

    internal bool IsAutoNextFloor(nint floor)
    {
        nint next = Read(_floorNext, floor, nint.Zero);
        return Read(_floorAuto, next, (byte)0) != 0;
    }

    internal bool IsPlayerAuto(nint player)
    {
        try
        {
            return _getPlayerAuto?.InvokeUnbox<byte>(player) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal bool IsNoFailInfinite(nint controller, nint player)
    {
        return Read(_controllerNoFailInfinite, controller, (byte)0) != 0
            || Read(_playerMidspinInfinite, player, (byte)0) != 0;
    }

    internal bool GetClockwise(nint player)
    {
        nint system = Read(_playerPlanetarySystem, player, nint.Zero);
        return Read(_systemClockwise, system, (byte)1) != 0;
    }

    internal double GetPlanetAngle(nint planet)
    {
        return Read(_planetAngle, planet, 0d);
    }

    internal double GetTargetExitAngle(nint planet)
    {
        return Read(_planetTargetExitAngle, planet, 0d);
    }

    internal int CalculateHitMargin(nint player, nint floor, nint planet)
    {
        nint system = Read(_playerPlanetarySystem, player, nint.Zero);
        double speed = Read(_systemSpeed, system, 1d);
        nint nextFloor = Read(_floorNext, floor, nint.Zero);
        double marginScale = Read(_floorMarginScale, nextFloor, 1d);
        return GameHooks.CalculateHitMargin(
            (float)GetPlanetAngle(planet),
            (float)GetTargetExitAngle(planet),
            GetClockwise(player),
            (float)(GetBpm() * speed),
            GetPitch(),
            marginScale);
    }

    internal void SetPlanetAngle(nint planet, double angle)
    {
        Write(_planetAngle, planet, angle);
        Write(_planetCachedAngle, planet, angle);
    }

    internal bool SetNoFailInfinite(nint controller, bool enabled)
    {
        bool previous = Read(_controllerNoFailInfinite, controller, (byte)0) != 0;
        Write(_controllerNoFailInfinite, controller, (byte)(enabled ? 1 : 0));
        return previous;
    }

    internal void PrepareReplayHit(nint controller, nint player)
    {
        Write(_controllerMultipressPenalty, controller, (byte)0);
        Write(_controllerMultipressFirst, controller, (byte)0);
        Write(_playerConsecutiveMultipress, player, 0);

        nint failBar = Read(_playerFailBar, player, nint.Zero);
        Write(_failBarMultipressCounter, failBar, 0f);
        Write(_failBarOverloadCounter, failBar, 0f);

        nint keyTimes = Read(_playerKeyTimes, player, nint.Zero);
        if (keyTimes == 0)
            return;
        try
        {
            new RuntimeObject(keyTimes).InvokeVoid("Clear", 0);
        }
        catch
        {
        }
    }

    internal int GetLanguageCode()
    {
        return Read(_language, 0, 10);
    }

    internal int GetCheckpoint()
    {
        return Read(_checkpoint, 0, 0);
    }

    internal float GetBpm()
    {
        return Read(_conductorBpm, GetConductor(), 0f);
    }

    internal float GetPitch()
    {
        nint song = Read(_conductorSong, GetConductor(), nint.Zero);
        if (song == 0)
            return 1f;
        try
        {
            float pitch = new RuntimeObject(song).InvokeUnbox<float>("get_pitch", 0);
            return Math.Abs(pitch) > 0.0001f ? pitch : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    internal bool IsOfficialLevel()
    {
        try
        {
            return _getIsOfficial?.InvokeStaticUnbox<byte>() != 0;
        }
        catch
        {
            return false;
        }
    }

    internal string GetSongName(nint controller)
    {
        nint game = Read(_gameInstance, 0, nint.Zero);
        nint levelData = Read(_gameLevelData, game, nint.Zero);
        string customSong = InvokeString(_getLevelSong, levelData);
        if (!string.IsNullOrWhiteSpace(customSong))
            return customSong;
        string value = ReadString(_controllerLevelName, controller);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        return GetCurrentLevelName();
    }

    internal string GetArtistName()
    {
        nint game = Read(_gameInstance, 0, nint.Zero);
        nint levelData = Read(_gameLevelData, game, nint.Zero);
        string artist = InvokeString(_getLevelArtist, levelData);
        return !string.IsNullOrWhiteSpace(artist) ? artist : ReadString(_levelArtist, levelData);
    }

    internal string GetLevelPath()
    {
        nint game = Read(_gameInstance, 0, nint.Zero);
        return ReadString(_gameLevelPath, game);
    }

    internal string GetSceneName()
    {
        string scene = ReadString(_sceneToLoad, 0);
        if (!string.IsNullOrWhiteSpace(scene))
            return scene;
        scene = InvokeStaticString(_getSceneName);
        return !string.IsNullOrWhiteSpace(scene) ? scene : GetCurrentLevelName();
    }

    internal string GetLevelId()
    {
        string levelId = ReadString(_internalLevelName, 0);
        if (IsLoadableOfficialLevelId(levelId))
            return levelId;
        levelId = ReadString(_sceneToLoad, 0);
        if (IsLoadableOfficialLevelId(levelId))
            return levelId;
        levelId = GetCurrentLevelName();
        return IsLoadableOfficialLevelId(levelId) ? levelId : "";
    }

    internal bool LoadReplayLevel(ReplayData replay)
    {
        LastLoadError = "";
        LastLoadRoute = "";
        try
        {
            nint controller = GetController();
            if (controller == 0)
                return FailLoad("游戏控制器尚未就绪。");
            if (IsSameLevel(replay))
            {
                ApplyReplayGlobals(replay);
                LastLoadRoute = "Restart";
                return Restart(controller) || FailLoad("无法重新加载当前谱面。");
            }

            if (replay.IsOfficialLevel)
            {
                string levelId = GetReplayLevelId(replay);
                if (!IsLoadableOfficialLevelId(levelId))
                    return FailLoad("回放缺少可加载的官方关卡 ID，请先打开对应谱面后回放。");
                if (_enterLevel == null)
                    return FailLoad("当前游戏版本没有官方关卡加载接口。");
                RuntimeString level = RuntimeString.New(_domain, levelId);
                if (!level.IsValid)
                    return FailLoad("无法创建官方关卡 ID。");
                ApplyReplayGlobals(replay);
                ResetLevelDestination();
                LastLoadRoute = "scrController.EnterLevel";
                _enterLevel(controller, level.Ptr, 0, _enterLevelMethodInfo);
                ApplyReplayGlobals(replay);
                return HasTargetScene(customLevel: false)
                    || FailLoad("官方关卡加载接口没有设置目标场景。");
            }

            if (string.IsNullOrWhiteSpace(replay.LevelPath) || !File.Exists(replay.LevelPath))
                return FailLoad("找不到回放对应的自定义谱面文件。");
            if (!CustomLevelMatchesReplay(replay.LevelPath, replay.SongName, out string actualSong))
                return FailLoad($"回放歌曲“{replay.SongName}”与目标谱面“{actualSong}”不匹配，已阻止加载以避免崩溃。");
            RuntimeString path = RuntimeString.New(_domain, replay.LevelPath);
            if (!path.IsValid)
                return FailLoad("无法创建自定义谱面路径。");

            nint currentGame = Read(_gameInstance, 0, nint.Zero);
            if (currentGame != 0
                && IsGameWorld(controller)
                && _loadAndPlayLevel != null)
            {
                ApplyReplayGlobals(replay);
                LastLoadRoute = "scnGame.LoadAndPlayLevel";
                return _loadAndPlayLevel(currentGame, path.Ptr, _loadAndPlayLevelMethodInfo) != 0
                    || FailLoad("当前游戏场景无法热重载目标自定义谱面。");
            }

            if (_loadCustomLevel != null)
            {
                ApplyReplayGlobals(replay);
                ResetLevelDestination();
                LastLoadRoute = "scrController.LoadCustomLevel";
                _loadCustomLevel(
                    controller,
                    path.Ptr,
                    nint.Zero,
                    0,
                    _loadCustomLevelMethodInfo);
                ApplyReplayGlobals(replay);
                return HasTargetScene(customLevel: true)
                    && Read(_customLevelPaths, 0, nint.Zero) != 0
                    || FailLoad("游戏没有建立自定义谱面加载请求。");
            }

            if (_enterLevel == null)
                return FailLoad("当前游戏版本没有可用的游戏场景入口。");
            RuntimeString bridgeLevel = RuntimeString.New(_domain, "1-X");
            if (!bridgeLevel.IsValid)
                return FailLoad("无法创建游戏场景过渡关卡 ID。");
            Write(_checkpoint, 0, 0);
            ResetLevelDestination();
            LastLoadRoute = "scrController.EnterLevel (custom replay bridge)";
            _enterLevel(controller, bridgeLevel.Ptr, 0, _enterLevelMethodInfo);
            return HasTargetScene(customLevel: false)
                || FailLoad("无法进入用于加载自定义回放的游戏场景。");
        }
        catch (Exception exception)
        {
            return FailLoad(exception.Message);
        }
    }

    internal string GetReplayLoadState()
    {
        nint customPaths = Read(_customLevelPaths, 0, nint.Zero);
        return $"scene='{ReadString(_sceneToLoad, 0)}', "
            + $"internal='{ReadString(_internalLevelName, 0)}', "
            + $"customPaths=0x{customPaths:X}, customIndex={Read(_customLevelIndex, 0, 0)}";
    }

    private bool IsSameLevel(ReplayData replay)
    {
        nint controller = GetController();
        if (!TryGetLevelIdentity(controller, out ReplayLevelIdentity? current, out _)
            || current == null
            || replay.IsOfficialLevel != current.IsOfficialLevel)
            return false;
        if (replay.IsOfficialLevel)
        {
            string replayLevelId = GetReplayLevelId(replay);
            if (!string.IsNullOrWhiteSpace(replayLevelId))
                return string.Equals(current.LevelId, replayLevelId, StringComparison.OrdinalIgnoreCase);
            return string.Equals(current.SceneName, replay.SceneName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.SongName, replay.SongName, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(replay.LevelPath) || !File.Exists(replay.LevelPath))
            return string.Equals(
                NormalizeTitle(current.SongName),
                NormalizeTitle(replay.SongName),
                StringComparison.OrdinalIgnoreCase);
        try
        {
            return string.Equals(
                Path.GetFullPath(current.LevelPath),
                Path.GetFullPath(replay.LevelPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(current.LevelPath, replay.LevelPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool Restart(nint controller)
    {
        if (_restart == null)
            return false;
        _restart(controller, 0, _restartMethodInfo);
        return true;
    }

    private void ApplyReplayGlobals(ReplayData replay)
    {
        float speed = replay.Speed > 0f ? replay.Speed : 1f;
        Write(_checkpoint, 0, replay.StartTile);
        Write(_currentSpeedTrial, 0, speed);
        Write(_nextSpeedRun, 0, speed);
    }

    private void ResetLevelDestination()
    {
        Write(_sceneToLoad, 0, nint.Zero);
        Write(_internalLevelName, 0, nint.Zero);
        Write(_customLevelPaths, 0, nint.Zero);
        Write(_customLevelId, 0, nint.Zero);
        Write(_loadCustomFromBundle, 0, (byte)0);
        Write(_customLevelIndex, 0, 0);
    }

    private bool CanCreateIslandEntry()
    {
        return _findGameObject != null
            && _instantiateWithParent != null
            && _getComponentTransform != null
            && _getGameObjectTransform != null
            && _getComponentGameObject != null
            && _setTransformPosition != null
            && _setObjectName != null
            && _setGameObjectActive != null;
    }

    private void TryCreateReplayIslandLabel(string label)
    {
        nint originalLabel = FindGameObject("Calibration");
        nint canvas = FindGameObject("Canvas World");
        nint parent = GetGameObjectTransform(canvas);
        if (originalLabel == 0 || parent == 0)
            return;
        nint replayLabel = _instantiateWithParent?.Invoke(
            originalLabel,
            parent,
            _instantiateWithParentMethodInfo) ?? 0;
        if (replayLabel == 0)
            return;
        SetObjectName(replayLabel, "Replay");
        SetPosition(GetGameObjectTransform(replayLabel), new NativeVector3(2.7348f, 4.1518f, 72.32f));
        nint textChanger = GetComponent(replayLabel, "scrTextChanger");
        if (textChanger != 0)
            _destroyObject?.Invoke(textChanger, _destroyObjectMethodInfo);
        nint text = GetComponent(replayLabel, "Text");
        if (text == 0)
            text = GetComponent(replayLabel, "UnityEngine.UI.Text");
        if (text != 0)
        {
            RuntimeString value = RuntimeString.New(_domain, "Replay");
            if (value.IsValid)
            {
                _setText?.Invoke(text, value.Ptr, _setTextMethodInfo);
                Write(_textValue, text, value.Ptr);
            }
        }
        _setGameObjectActive?.Invoke(replayLabel, 1, _setGameObjectActiveMethodInfo);
        _replayIslandLabelObject = replayLabel;
    }

    private nint FindGameObject(string name)
    {
        if (_findGameObject == null)
            return 0;
        RuntimeString value = RuntimeString.New(_domain, name);
        return value.IsValid ? _findGameObject(value.Ptr, _findGameObjectMethodInfo) : 0;
    }

    private nint GetComponent(nint gameObject, string typeName)
    {
        if (gameObject == 0 || _getComponentByName == null)
            return 0;
        RuntimeString value = RuntimeString.New(_domain, typeName);
        return value.IsValid
            ? _getComponentByName(gameObject, value.Ptr, _getComponentByNameMethodInfo)
            : 0;
    }

    private nint GetComponentTransform(nint component)
    {
        return component == 0
            ? 0
            : _getComponentTransform?.Invoke(component, _getComponentTransformMethodInfo) ?? 0;
    }

    private nint GetGameObjectTransform(nint gameObject)
    {
        return gameObject == 0
            ? 0
            : _getGameObjectTransform?.Invoke(gameObject, _getGameObjectTransformMethodInfo) ?? 0;
    }

    private nint GetGameObject(nint component)
    {
        return component == 0
            ? 0
            : _getComponentGameObject?.Invoke(component, _getComponentGameObjectMethodInfo) ?? 0;
    }

    private void SetObjectName(nint instance, string name)
    {
        if (instance == 0 || _setObjectName == null)
            return;
        RuntimeString value = RuntimeString.New(_domain, name);
        if (value.IsValid)
            _setObjectName(instance, value.Ptr, _setObjectNameMethodInfo);
    }

    private void SetPosition(nint transform, NativeVector3 position)
    {
        if (transform != 0)
            _setTransformPosition?.Invoke(transform, position, _setTransformPositionMethodInfo);
    }

    private bool FailIslandEntry(string message)
    {
        LastIslandEntryError = message;
        return false;
    }

    private bool HasTargetScene(bool customLevel)
    {
        string scene = ReadString(_sceneToLoad, 0);
        if (customLevel && string.IsNullOrWhiteSpace(scene))
        {
            scene = "scnGame";
            WriteString(_sceneToLoad, scene);
        }
        return !string.IsNullOrWhiteSpace(scene);
    }

    private static bool CustomLevelMatchesReplay(string path, string replaySong, out string actualSong)
    {
        actualSong = "";
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (!document.RootElement.TryGetProperty("settings", out JsonElement settings)
                || !settings.TryGetProperty("song", out JsonElement song)
                || song.ValueKind != JsonValueKind.String)
                return true;
            actualSong = song.GetString()?.Trim() ?? "";
            string expected = NormalizeTitle(replaySong);
            string actual = NormalizeTitle(actualSong);
            return string.IsNullOrEmpty(expected)
                || string.IsNullOrEmpty(actual)
                || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        bool insideTag = false;
        foreach (char character in value)
        {
            if (character == '<')
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
            if (!char.IsWhiteSpace(character))
                buffer[length++] = character;
        }
        return new string(buffer[..length]);
    }

    private bool FailLoad(string message)
    {
        LastLoadError = message;
        return false;
    }

    private static string GetReplayLevelId(ReplayData replay)
    {
        if (IsLoadableOfficialLevelId(replay.LevelId))
            return replay.LevelId;
        if (IsLoadableOfficialLevelId(replay.SceneName))
            return replay.SceneName;
        return IsLoadableOfficialLevelId(replay.SongName) ? replay.SongName : "";
    }

    private static bool IsLoadableOfficialLevelId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsWhiteSpace))
            return false;
        string[] parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(part => part.Any(character => !char.IsLetterOrDigit(character))))
            return false;
        string suffix = parts[^1];
        return string.Equals(suffix, "X", StringComparison.OrdinalIgnoreCase)
            || int.TryParse(suffix, out _);
    }

    private string GetCurrentLevelName()
    {
        try
        {
            nint value = _getCurrentLevel?.InvokeStatic() ?? 0;
            return value == 0 ? "" : new RuntimeString(value).ToString();
        }
        catch
        {
            return "";
        }
    }

    private IRuntimeClass RequireClass(string namespaze, string name)
    {
        return FindClass(namespaze, name)
            ?? throw new InvalidOperationException($"Game class not found: {namespaze}.{name}");
    }

    private IRuntimeClass? FindClass(string namespaze, string name)
    {
        return _gameAssembly.GetClass(namespaze, name);
    }

    private IRuntimeClass? FindClassInDomain(string namespaze, string name)
    {
        foreach (IRuntimeAssembly assembly in _domain.GetAssemblies())
        {
            try
            {
                IRuntimeClass? type = assembly.GetClass(namespaze, name);
                if (type != null)
                    return type;
            }
            catch
            {
            }
        }
        return null;
    }

    private static TDelegate? Bind<TDelegate>(
        IRuntimeClass? type,
        string methodName,
        string[] parameterTypes,
        out nint methodInfo) where TDelegate : Delegate
    {
        methodInfo = 0;
        IRuntimeMethod? method = type?.GetMethod(methodName, parameterTypes);
        nint function = method?.FunctionPtr ?? 0;
        if (method == null || function == 0)
            return null;
        methodInfo = method.Ptr;
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(function);
    }

    private static IRuntimeField? FindField(IRuntimeClass? type, params string[] names)
    {
        if (type == null)
            return null;
        foreach (string name in names)
        {
            IRuntimeField? field = type.GetField(name);
            if (field != null)
                return field;
        }
        return null;
    }

    private static nint InvokeStaticObject(IRuntimeMethod? method)
    {
        try
        {
            return method?.InvokeStatic() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string InvokeString(IRuntimeMethod? method, nint instance)
    {
        if (method == null || instance == 0)
            return "";
        try
        {
            nint value = method.Invoke(instance);
            return value == 0 ? "" : new RuntimeString(value).ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string InvokeStaticString(IRuntimeMethod? method)
    {
        if (method == null)
            return "";
        try
        {
            nint value = method.InvokeStatic();
            return value == 0 ? "" : new RuntimeString(value).ToString();
        }
        catch
        {
            return "";
        }
    }

    private static T Read<T>(IRuntimeField? field, nint instance, T fallback) where T : unmanaged
    {
        if (field == null || (!field.IsStatic && instance == 0))
            return fallback;
        try
        {
            return field.GetValue<T>(instance);
        }
        catch
        {
            return fallback;
        }
    }

    private static void Write<T>(IRuntimeField? field, nint instance, T value) where T : unmanaged
    {
        if (field == null || (!field.IsStatic && instance == 0))
            return;
        try
        {
            field.SetValue(instance, value);
        }
        catch
        {
        }
    }

    private static string ReadString(IRuntimeField? field, nint instance)
    {
        nint value = Read(field, instance, nint.Zero);
        if (value == 0)
            return "";
        try
        {
            return new RuntimeString(value).ToString();
        }
        catch
        {
            return "";
        }
    }

    private void WriteString(IRuntimeField? field, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Write(field, 0, nint.Zero);
            return;
        }
        RuntimeString runtimeValue = RuntimeString.New(_domain, value);
        if (runtimeValue.IsValid)
            Write(field, 0, runtimeValue.Ptr);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RestartDelegate(nint instance, byte skipTransition, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetBooleanDelegate(nint instance, byte value, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EnterLevelDelegate(nint instance, nint levelId, byte speedTrial, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoadSceneDelegate(nint sceneName, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoadCustomLevelDelegate(
        nint instance,
        nint path,
        nint customLevelId,
        byte fromBundle,
        nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte LoadAndPlayLevelDelegate(nint instance, nint path, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FindObjectDelegate(nint name, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint InstantiateWithParentDelegate(nint original, nint parent, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetObjectDelegate(nint instance, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetComponentByNameDelegate(nint instance, nint typeName, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetStringDelegate(nint instance, nint value, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetVector3Delegate(nint instance, NativeVector3 value, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyObjectDelegate(nint instance, nint methodInfo);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeVector3(float x, float y, float z)
    {
        internal readonly float X = x;
        internal readonly float Y = y;
        internal readonly float Z = z;
    }
}
