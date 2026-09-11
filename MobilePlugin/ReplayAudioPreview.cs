using System.Runtime.InteropServices;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace Replay.Mobile;

/// <summary>
/// Local chart audio preview using the game's native audio objects:
/// first use the game's AudioManager cache, then load the loose file through
/// UnityWebRequest, and finally play only the configured preview segment.
/// All calls are made from Replay's existing controller-update hook.
/// </summary>
internal sealed unsafe class ReplayAudioPreview : IDisposable
{
    private const string LogTag = "Replay";
    private const int MaxLoadingFrames = 900;

    private readonly IAppDomain _domain;
    private readonly IRuntimeAssembly _assembly;
    private readonly IRuntimeClass? _adoBaseClass;
    private readonly IRuntimeClass? _audioManagerClass;
    private readonly IRuntimeClass? _conductorClass;
    private readonly IRuntimeClass? _audioSourceClass;
    private readonly IRuntimeClass? _unityWebRequestMultimediaClass;
    private readonly IRuntimeClass? _unityWebRequestClass;
    private readonly IRuntimeClass? _downloadHandlerAudioClipClass;
    private readonly IRuntimeClass? _audioClipClass;
    private readonly IRuntimeClass? _previewSongPlayerClass;
    private readonly IRuntimeClass? _customLevelSelectClass;

    private readonly IRuntimeField? _customLevelSelectInstance;
    private readonly IRuntimeField? _customLevelSelectPreviewSongPlayer;
    private readonly IRuntimeField? _conductorInstance;
    private readonly IRuntimeField? _conductorSong;
    private readonly IRuntimeField? _conductorSong2;
    private readonly IRuntimeField? _conductorSong3;
    private readonly IRuntimeField? _previewSongPlayerAudioSource;

    private readonly IRuntimeMethod? _getAudioManager;
    private readonly IRuntimeMethod? _findOrLoadAudioClip;
    private readonly IRuntimeMethod? _unityGetAudioClip;
    private readonly IRuntimeMethod? _unityWebRequestSend;
    private readonly IRuntimeMethod? _unityWebRequestIsDone;
    private readonly IRuntimeMethod? _unityWebRequestDownloadHandler;
    private readonly IRuntimeMethod? _unityWebRequestGetError;
    private readonly IRuntimeMethod? _unityWebRequestAbort;
    private readonly IRuntimeMethod? _unityWebRequestDispose;
    private readonly IRuntimeMethod? _downloadHandlerAudioClipGetContent;
    private readonly IRuntimeMethod? _downloadHandlerAudioClipGetClip;
    private readonly IRuntimeMethod? _downloadHandlerAudioClipSetStream;
    private readonly IRuntimeMethod? _audioClipIsReadyToPlay;
    private readonly IRuntimeMethod? _audioClipGetLength;
    private readonly IRuntimeMethod? _previewSongPlayerStop;
    private readonly IRuntimeMethod? _previewSongPlayerPlay;
    private readonly IRuntimeMethod? _audioSourceGetClip;
    private readonly IRuntimeMethod? _audioSourceSetClip;
    private readonly IRuntimeMethod? _audioSourceGetLoop;
    private readonly IRuntimeMethod? _audioSourceSetLoop;
    private readonly IRuntimeMethod? _audioSourceGetVolume;
    private readonly IRuntimeMethod? _audioSourceSetVolume;
    private readonly IRuntimeMethod? _audioSourceGetTime;
    private readonly IRuntimeMethod? _audioSourceSetTime;
    private readonly IRuntimeMethod? _audioSourceGetIsPlaying;
    private readonly IRuntimeMethod? _audioSourcePlay;
    private readonly IRuntimeMethod? _audioSourceStop;

    private readonly List<AudioSourceState> _sourceStates = new();
    private nint _previewSource;
    private nint _previewClip;
    private nint _previewSongPlayer;
    private float _previewStart;
    private float _previewEnd;
    private bool _previewActive;

    private bool _audioLoading;
    private int _audioLoadingFrames;
    private nint _pendingAudioManager;
    private nint _pendingAudioConductor;
    private nint _pendingAudioRequest;
    private nint _pendingAudioAsyncOperation;
    private nint _pendingAudioClip;
    private ReplayChartAudioPreview? _pendingPreview;
    private bool _pendingAudioRequestComplete;

    internal bool IsActive => _previewActive || _audioLoading;

    internal ReplayAudioPreview(IAppDomain domain, IRuntimeAssembly assembly)
    {
        _domain = domain;
        _assembly = assembly;
        _adoBaseClass = FindClass("ADOBase");
        _audioManagerClass = FindClass("AudioManager");
        _conductorClass = FindClass("scrConductor");
        _audioSourceClass = FindClassInDomain("UnityEngine", "AudioSource");
        _unityWebRequestMultimediaClass = FindClassInDomain(
            "UnityEngine.Networking", "UnityWebRequestMultimedia");
        _unityWebRequestClass = FindClassInDomain(
            "UnityEngine.Networking", "UnityWebRequest");
        _downloadHandlerAudioClipClass = FindClassInDomain(
            "UnityEngine.Networking", "DownloadHandlerAudioClip");
        _audioClipClass = FindClassInDomain("UnityEngine", "AudioClip");
        _previewSongPlayerClass = FindClass("PreviewSongPlayer");
        _customLevelSelectClass = FindClass("scnCLS");

        _customLevelSelectInstance = FindField(_customLevelSelectClass, "instance", "_instance");
        _customLevelSelectPreviewSongPlayer = FindField(
            _customLevelSelectClass,
            "previewSongPlayer");
        _conductorInstance = FindField(_conductorClass, "_instance", "instance");
        _conductorSong = FindField(_conductorClass, "song");
        _conductorSong2 = FindField(_conductorClass, "song2");
        _conductorSong3 = FindField(_conductorClass, "song3");
        _previewSongPlayerAudioSource = FindField(_previewSongPlayerClass, "audioSource");

        _getAudioManager = _adoBaseClass?.GetMethod("get_audioManager", 0)
            ?? _audioManagerClass?.GetMethod("get_Instance", 0);
        _findOrLoadAudioClip = _audioManagerClass?.GetMethod("FindOrLoadAudioClip", 3);
        _unityGetAudioClip = _unityWebRequestMultimediaClass?.GetMethod("GetAudioClip", 2);
        _unityWebRequestSend = _unityWebRequestClass?.GetMethod("SendWebRequest", 0);
        _unityWebRequestIsDone = _unityWebRequestClass?.GetMethod("get_isDone", 0);
        _unityWebRequestDownloadHandler = _unityWebRequestClass?.GetMethod("get_downloadHandler", 0);
        _unityWebRequestGetError = _unityWebRequestClass?.GetMethod("get_error", 0);
        _unityWebRequestAbort = _unityWebRequestClass?.GetMethod("Abort", 0);
        _unityWebRequestDispose = _unityWebRequestClass?.GetMethod("Dispose", 0);
        _downloadHandlerAudioClipGetContent = _downloadHandlerAudioClipClass?.GetMethod("GetContent", 1);
        _downloadHandlerAudioClipGetClip = _downloadHandlerAudioClipClass?.GetMethod("get_audioClip", 0);
        _downloadHandlerAudioClipSetStream = _downloadHandlerAudioClipClass?.GetMethod("set_streamAudio", 1);
        _audioClipIsReadyToPlay = _audioClipClass?.GetMethod("get_isReadyToPlay", 0);
        _audioClipGetLength = _audioClipClass?.GetMethod("get_length", 0);
        _previewSongPlayerStop = _previewSongPlayerClass?.GetMethod("Stop", 0);
        _previewSongPlayerPlay = _previewSongPlayerClass?.GetMethod("Play", 4);
        _audioSourceGetClip = _audioSourceClass?.GetMethod("get_clip", 0);
        _audioSourceSetClip = _audioSourceClass?.GetMethod("set_clip", 1);
        _audioSourceGetLoop = _audioSourceClass?.GetMethod("get_loop", 0);
        _audioSourceSetLoop = _audioSourceClass?.GetMethod("set_loop", 1);
        _audioSourceGetVolume = _audioSourceClass?.GetMethod("get_volume", 0);
        _audioSourceSetVolume = _audioSourceClass?.GetMethod("set_volume", 1);
        _audioSourceGetTime = _audioSourceClass?.GetMethod("get_time", 0);
        _audioSourceSetTime = _audioSourceClass?.GetMethod("set_time", 1);
        _audioSourceGetIsPlaying = _audioSourceClass?.GetMethod("get_isPlaying", 0);
        _audioSourcePlay = _audioSourceClass?.GetMethod("Play", 0);
        _audioSourceStop = _audioSourceClass?.GetMethod("Stop", 0);
    }

    internal bool Start(ReplayChartAudioPreview preview, nint conductor)
    {
        Stop();
        if (preview == null
            || string.IsNullOrWhiteSpace(preview.SongPath)
            || !File.Exists(preview.SongPath)
            || conductor == 0
            || _audioSourceClass == null
            || _audioSourceSetClip == null
            || _audioSourcePlay == null
            || _audioSourceStop == null)
        {
            Logger.Debug(LogTag, "预览音乐接口、谱面路径或 conductor 不可用。");
            return false;
        }

        if (PrepareAudioPreviewSources(conductor) == 0)
            return false;

        nint audioManager = InvokeStaticObject(_getAudioManager);
        nint clip = audioManager == 0 ? 0 : TryLoadAudioClip(audioManager, preview);
        if (clip != 0 && StartLoadedPreview(preview, conductor, clip))
            return true;

        if (StartUnityWebRequestAudioLoad(audioManager, conductor, preview))
            return true;

        Logger.Debug(LogTag, $"预览音乐加载失败: {preview.SongPath}");
        Stop();
        return false;
    }

    internal void Tick()
    {
        if (_audioLoading)
        {
            _audioLoadingFrames++;
            if (_pendingPreview == null || _pendingAudioConductor == 0)
            {
                Stop();
                return;
            }

            if (_pendingAudioRequest == 0)
                return;
            if (_pendingAudioRequestComplete)
            {
                if (_pendingAudioClip != 0 && IsAudioClipReady(_pendingAudioClip))
                {
                    nint clip = _pendingAudioClip;
                    DisposePendingUnityWebRequest();
                    CompletePendingAudio(clip);
                }
                else if (_audioLoadingFrames > MaxLoadingFrames)
                {
                    Logger.Debug(LogTag, "Unity 音频解码超时。");
                    Stop();
                }
                return;
            }

            if (ReadByte(_unityWebRequestIsDone, _pendingAudioRequest, 0) != 0)
            {
                nint clip = ReadUnityWebRequestAudioClip(_pendingAudioRequest);
                string error = ReadInstanceString(_unityWebRequestGetError, _pendingAudioRequest);
                if (clip != 0)
                {
                    _pendingAudioClip = clip;
                    _pendingAudioRequestComplete = true;
                    if (IsAudioClipReady(clip))
                    {
                        DisposePendingUnityWebRequest();
                        CompletePendingAudio(clip);
                    }
                    return;
                }

                Logger.Debug(LogTag, $"UnityWebRequest 音频加载失败: {_pendingPreview.SongPath}, error={error}");
                Stop();
                return;
            }

            if (_audioLoadingFrames > MaxLoadingFrames)
            {
                Logger.Debug(LogTag, "UnityWebRequest 音频加载超时。");
                Stop();
            }
            return;
        }

        // PreviewSongPlayer 自带循环；这里只处理没有该组件时的 AudioSource 回退。
        if (!_previewActive || _previewSource == 0 || _audioSourceGetTime == null)
            return;
        float time = ReadFloat(_audioSourceGetTime, _previewSource, _previewStart);
        bool playing = ReadByte(_audioSourceGetIsPlaying, _previewSource, 1) != 0;
        if (!playing || time < _previewStart - 0.25f || time >= _previewEnd - 0.02f)
        {
            SetFloat(_audioSourceSetTime, _previewSource, _previewStart);
            InvokeInstanceVoid(_audioSourcePlay, _previewSource);
        }
    }

    internal void Stop()
    {
        if (_audioLoading && _pendingAudioRequest != 0)
            DisposePendingUnityWebRequest();
        if ((_audioLoading || _previewActive) && _previewSongPlayer != 0)
            InvokeInstanceVoid(_previewSongPlayerStop, _previewSongPlayer);

        _audioLoading = false;
        _audioLoadingFrames = 0;
        _pendingPreview = null;
        _pendingAudioManager = 0;
        _pendingAudioConductor = 0;
        _pendingAudioAsyncOperation = 0;
        _pendingAudioClip = 0;
        _pendingAudioRequestComplete = false;

        if (!_previewActive && _sourceStates.Count == 0)
            return;
        if (_previewSource != 0)
            InvokeInstanceVoid(_audioSourceStop, _previewSource);

        foreach (AudioSourceState state in _sourceStates)
        {
            if (state.Source == 0)
                continue;
            SetObject(_audioSourceSetClip, state.Source, state.Clip);
            SetByte(_audioSourceSetLoop, state.Source, state.Loop);
            SetFloat(_audioSourceSetVolume, state.Source, state.Volume);
            SetFloat(_audioSourceSetTime, state.Source, state.Time);
            if (state.Playing)
                InvokeInstanceVoid(_audioSourcePlay, state.Source);
        }

        _sourceStates.Clear();
        _previewSource = 0;
        _previewClip = 0;
        _previewSongPlayer = 0;
        _previewActive = false;
    }

    public void Dispose() => Stop();

    private bool StartLoadedPreview(ReplayChartAudioPreview preview, nint conductor, nint clip)
    {
        if (clip == 0)
            return false;

        nint source = _previewSource != 0 ? _previewSource : FindConductorAudioSource(conductor);
        float clipLength = ReadFloat(_audioClipGetLength, clip, 0f);
        float start = Math.Max(0f, preview.PreviewSongStart);
        if (clipLength > 0f)
            start = Math.Clamp(start, 0f, Math.Max(0f, clipLength - 0.05f));
        float duration = preview.PreviewSongDuration > 0
            ? preview.PreviewSongDuration
            : clipLength > start ? clipLength - start : 10f;
        duration = Math.Max(0.05f, duration);
        float end = start + duration;
        if (clipLength > 0f)
            end = Math.Min(end, clipLength);
        if (end <= start + 0.05f)
            end = clipLength > start ? clipLength : start + 10f;

        foreach (AudioSourceState state in _sourceStates)
            InvokeInstanceVoid(_audioSourceStop, state.Source);

        if (_previewSongPlayer != 0
            && Read(_previewSongPlayerAudioSource, _previewSongPlayer, nint.Zero) != 0
            && _previewSongPlayerPlay != null)
        {
            float nativeDuration = Math.Max(0.05f, end - start);
            float volume = Math.Clamp(preview.Volume / 100f, 0f, 1f);
            if (InvokeRuntimeMethod(
                    _previewSongPlayerPlay,
                    _previewSongPlayer,
                    new[]
                    {
                        clip,
                        (nint)(&start),
                        (nint)(&nativeDuration),
                        (nint)(&volume),
                    },
                    out _))
            {
                _previewSource = 0;
                _previewClip = clip;
                _previewStart = start;
                _previewEnd = end;
                _previewActive = true;
                Logger.Info(LogTag, $"开始循环预览音乐: {preview.SongPath}, start={start:0.##}, duration={nativeDuration:0.##}");
                return true;
            }
        }

        if (source == 0 || _audioSourceGetTime == null || _audioSourceSetTime == null
            || _audioSourceGetIsPlaying == null)
            return false;
        if (_sourceStates.Count == 0)
            CaptureAudioSource(source);
        SetObject(_audioSourceSetClip, source, clip);
        SetByte(_audioSourceSetLoop, source, false);
        SetFloat(_audioSourceSetVolume, source, Math.Clamp(preview.Volume / 100f, 0f, 1f));
        SetFloat(_audioSourceSetTime, source, start);
        InvokeInstanceVoid(_audioSourcePlay, source);
        _previewSource = source;
        _previewClip = clip;
        _previewStart = start;
        _previewEnd = end;
        _previewActive = true;
        Logger.Info(LogTag, $"开始循环预览音乐: {preview.SongPath}, start={start:0.##}, end={end:0.##}");
        return true;
    }

    private bool StartUnityWebRequestAudioLoad(
        nint audioManager,
        nint conductor,
        ReplayChartAudioPreview preview)
    {
        if (_unityGetAudioClip == null || _unityWebRequestSend == null
            || _unityWebRequestIsDone == null)
            return false;

        string uri;
        try { uri = new Uri(Path.GetFullPath(preview.SongPath), UriKind.Absolute).AbsoluteUri; }
        catch { return false; }
        int audioType = GetUnityAudioType(preview.SongPath);
        RuntimeString runtimeUri = RuntimeString.New(_domain, uri);
        if (!runtimeUri.IsValid)
            return false;

        try
        {
            nint request = InvokeRuntimeStaticObject(
                _unityGetAudioClip,
                new[] { runtimeUri.Ptr, (nint)(&audioType) },
                out _);
            if (request == 0)
                return false;
            nint handler = InvokeInstanceObject(_unityWebRequestDownloadHandler, request);
            if (handler != 0)
            {
                byte stream = 1;
                SetByte(_downloadHandlerAudioClipSetStream, handler, stream);
            }
            nint operation = InvokeRuntimeObject(
                _unityWebRequestSend,
                request,
                null,
                out nint sendException);
            if (operation == 0 && sendException != 0)
            {
                DisposeUnityWebRequest(request);
                return false;
            }

            _audioLoading = true;
            _audioLoadingFrames = 0;
            _pendingAudioManager = audioManager;
            _pendingAudioConductor = conductor;
            _pendingAudioRequest = request;
            _pendingAudioAsyncOperation = operation;
            _pendingAudioClip = 0;
            _pendingPreview = preview;
            _pendingAudioRequestComplete = false;
            Logger.Info(LogTag, $"开始加载预览音乐: {preview.SongPath}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private nint PrepareAudioPreviewSources(nint conductor)
    {
        _previewSongPlayer = GetNativePreviewSongPlayer();
        nint nativeSource = Read(_previewSongPlayerAudioSource, _previewSongPlayer, nint.Zero);
        nint source = nativeSource != 0 ? nativeSource : FindConductorAudioSource(conductor);
        if (source == 0)
            return 0;

        CaptureAudioSource(source);
        CaptureAudioSource(nativeSource);
        CaptureAudioSource(Read(_conductorSong, conductor, nint.Zero));
        CaptureAudioSource(Read(_conductorSong2, conductor, nint.Zero));
        CaptureAudioSource(Read(_conductorSong3, conductor, nint.Zero));
        if (_previewSongPlayer != 0)
            InvokeInstanceVoid(_previewSongPlayerStop, _previewSongPlayer);
        foreach (AudioSourceState state in _sourceStates)
            InvokeInstanceVoid(_audioSourceStop, state.Source);
        _previewSource = source;
        return source;
    }

    private nint GetNativePreviewSongPlayer()
    {
        IRuntimeMethod? getCls = _adoBaseClass?.GetMethod("get_cls", 0);
        nint customLevelSelect = InvokeStaticObject(getCls);
        if (customLevelSelect == 0)
            customLevelSelect = Read(_customLevelSelectInstance, 0, nint.Zero);
        return Read(_customLevelSelectPreviewSongPlayer, customLevelSelect, nint.Zero);
    }

    private nint FindConductorAudioSource(nint conductor)
    {
        return Read(_conductorSong, conductor, nint.Zero)
            != 0 ? Read(_conductorSong, conductor, nint.Zero)
            : Read(_conductorSong2, conductor, nint.Zero) != 0
                ? Read(_conductorSong2, conductor, nint.Zero)
                : Read(_conductorSong3, conductor, nint.Zero);
    }

    private nint TryLoadAudioClip(nint audioManager, ReplayChartAudioPreview preview)
    {
        if (_findOrLoadAudioClip == null || audioManager == 0)
            return 0;
        string directoryKey = Path.GetFileName(Path.GetDirectoryName(preview.SongPath) ?? "") ?? "";
        string[] names = { Path.GetFileName(preview.SongPath), preview.SongPath };
        string[] internalNames = { directoryKey, Path.GetDirectoryName(preview.SongPath) ?? "", "" };
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            RuntimeString clipName = RuntimeString.New(_domain, name);
            if (!clipName.IsValid)
                continue;
            foreach (string internalNameValue in internalNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RuntimeString internalName = RuntimeString.New(_domain, internalNameValue);
                if (!internalName.IsValid)
                    continue;
                byte fromBundle = 0;
                try
                {
                    nint result = _findOrLoadAudioClip.Invoke(
                        audioManager,
                        new[] { clipName.Ptr, internalName.Ptr, (nint)(&fromBundle) });
                    if (result != 0)
                        return result;
                }
                catch
                {
                }
            }
        }
        return 0;
    }

    private nint ReadUnityWebRequestAudioClip(nint request)
    {
        if (request == 0)
            return 0;
        if (_downloadHandlerAudioClipGetContent != null)
        {
            nint clip = InvokeRuntimeStaticObject(
                _downloadHandlerAudioClipGetContent,
                new[] { request },
                out _);
            if (clip != 0)
                return clip;
        }
        nint handler = InvokeInstanceObject(_unityWebRequestDownloadHandler, request);
        return InvokeInstanceObject(_downloadHandlerAudioClipGetClip, handler);
    }

    private void CompletePendingAudio(nint clip)
    {
        if (_pendingPreview == null || _pendingAudioConductor == 0 || clip == 0)
        {
            Stop();
            return;
        }
        ReplayChartAudioPreview preview = _pendingPreview;
        nint conductor = _pendingAudioConductor;
        _audioLoading = false;
        _pendingPreview = null;
        _pendingAudioManager = 0;
        _pendingAudioConductor = 0;
        _pendingAudioRequest = 0;
        _pendingAudioAsyncOperation = 0;
        _pendingAudioClip = 0;
        if (!StartLoadedPreview(preview, conductor, clip))
            Stop();
    }

    private void DisposePendingUnityWebRequest()
    {
        nint request = _pendingAudioRequest;
        if (request == 0)
            return;
        if (!_pendingAudioRequestComplete)
            InvokeInstanceVoid(_unityWebRequestAbort, request);
        InvokeInstanceVoid(_unityWebRequestDispose, request);
        _pendingAudioRequest = 0;
        _pendingAudioAsyncOperation = 0;
        _pendingAudioRequestComplete = false;
    }

    private void DisposeUnityWebRequest(nint request)
    {
        if (request == 0)
            return;
        InvokeInstanceVoid(_unityWebRequestAbort, request);
        InvokeInstanceVoid(_unityWebRequestDispose, request);
    }

    private bool IsAudioClipReady(nint clip)
        => clip != 0 && (_audioClipIsReadyToPlay == null || ReadByte(_audioClipIsReadyToPlay, clip, 0) != 0);

    private void CaptureAudioSource(nint source)
    {
        if (source == 0 || _sourceStates.Any(state => state.Source == source))
            return;
        _sourceStates.Add(new AudioSourceState
        {
            Source = source,
            Clip = InvokeInstanceObject(_audioSourceGetClip, source),
            Loop = ReadByte(_audioSourceGetLoop, source, 0) != 0,
            Volume = ReadFloat(_audioSourceGetVolume, source, 1f),
            Time = ReadFloat(_audioSourceGetTime, source, 0f),
            Playing = ReadByte(_audioSourceGetIsPlaying, source, 0) != 0,
        });
    }

    private static int GetUnityAudioType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".aac" => 1,
            ".aif" or ".aiff" => 2,
            ".it" => 10,
            ".mod" => 12,
            ".mp3" => 13,
            ".ogg" or ".oga" => 14,
            ".s3m" => 17,
            ".wav" => 20,
            ".xm" => 21,
            ".xma" => 22,
            ".vag" => 23,
            _ => 0,
        };

    private static void SetObject(IRuntimeMethod? method, nint instance, nint value)
    {
        if (method == null || instance == 0)
            return;
        try { method.Invoke(instance, new[] { value }); }
        catch { }
    }

    private static void SetFloat(IRuntimeMethod? method, nint instance, float value)
    {
        if (method == null || instance == 0)
            return;
        try { method.Invoke(instance, new[] { (nint)(&value) }); }
        catch { }
    }

    private static void SetByte(IRuntimeMethod? method, nint instance, byte value)
    {
        if (method == null || instance == 0)
            return;
        try { method.Invoke(instance, new[] { (nint)(&value) }); }
        catch { }
    }

    private static void SetByte(IRuntimeMethod? method, nint instance, bool value)
        => SetByte(method, instance, (byte)(value ? 1 : 0));

    private static void InvokeInstanceVoid(IRuntimeMethod? method, nint instance)
    {
        if (method == null || instance == 0)
            return;
        try { method.Invoke(instance); }
        catch { }
    }

    private static float ReadFloat(IRuntimeMethod? method, nint instance, float fallback)
    {
        if (method == null || instance == 0)
            return fallback;
        try { return method.InvokeUnbox<float>(instance); }
        catch { return fallback; }
    }

    private static byte ReadByte(IRuntimeMethod? method, nint instance, byte fallback)
    {
        if (method == null || instance == 0)
            return fallback;
        try { return method.InvokeUnbox<byte>(instance); }
        catch { return fallback; }
    }

    private static T Read<T>(IRuntimeField? field, nint instance, T fallback) where T : unmanaged
    {
        if (field == null || (!field.IsStatic && instance == 0))
            return fallback;
        try { return field.GetValue<T>(instance); }
        catch { return fallback; }
    }

    private static nint InvokeStaticObject(IRuntimeMethod? method)
    {
        try { return method?.InvokeStatic() ?? 0; }
        catch { return 0; }
    }

    private static nint InvokeInstanceObject(IRuntimeMethod? method, nint instance)
    {
        if (method == null || instance == 0)
            return 0;
        try { return method.Invoke(instance); }
        catch { return 0; }
    }

    private static string ReadInstanceString(IRuntimeMethod? method, nint instance)
    {
        nint value = InvokeInstanceObject(method, instance);
        return value == 0 ? "" : new RuntimeString(value).ToString();
    }

    private static unsafe bool InvokeRuntimeMethod(
        IRuntimeMethod? method,
        nint instance,
        nint[]? arguments,
        out nint exception)
    {
        exception = 0;
        if (method == null || instance == 0)
            return false;
        try
        {
            fixed (nint* argumentPointer = arguments)
                Il2CppFunctions.il2cpp_runtime_invoke(
                    method.Ptr,
                    instance,
                    (void**)argumentPointer,
                    ref exception);
            return exception == 0;
        }
        catch
        {
            exception = 1;
            return false;
        }
    }

    private static unsafe nint InvokeRuntimeObject(
        IRuntimeMethod? method,
        nint instance,
        nint[]? arguments,
        out nint exception)
    {
        exception = 0;
        if (method == null || instance == 0)
            return 0;
        try
        {
            fixed (nint* argumentPointer = arguments)
            {
                nint result = Il2CppFunctions.il2cpp_runtime_invoke(
                    method.Ptr,
                    instance,
                    (void**)argumentPointer,
                    ref exception);
                return exception == 0 ? result : 0;
            }
        }
        catch
        {
            exception = 1;
            return 0;
        }
    }

    private static unsafe nint InvokeRuntimeStaticObject(
        IRuntimeMethod? method,
        nint[]? arguments,
        out nint exception)
    {
        exception = 0;
        if (method == null)
            return 0;
        try
        {
            fixed (nint* argumentPointer = arguments)
            {
                nint result = Il2CppFunctions.il2cpp_runtime_invoke(
                    method.Ptr,
                    0,
                    (void**)argumentPointer,
                    ref exception);
                return exception == 0 ? result : 0;
            }
        }
        catch
        {
            exception = 1;
            return 0;
        }
    }

    private IRuntimeClass? FindClass(string name) => _assembly.GetClass("", name);

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

    private sealed class AudioSourceState
    {
        internal nint Source;
        internal nint Clip;
        internal bool Loop;
        internal float Volume;
        internal float Time;
        internal bool Playing;
    }
}
