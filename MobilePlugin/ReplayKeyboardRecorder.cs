using ImGuiNET;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace Replay.Mobile;

/// <summary>
/// 录制期间的键盘输入采集器。native initializeKeyEvent 是首选来源；Unity legacy
/// Input 只在确实录制时轮询，用于设备/游戏版本没有 native 符号的 fallback。
/// </summary>
internal static unsafe class ReplayKeyboardRecorder
{
    private const long BindRetryMilliseconds = 1000L;
    private static readonly Dictionary<ImGuiKey, bool> PreviousDown = new();
    private static readonly HashSet<ImGuiKey> NativeDownKeys = new();
    private static readonly object StateLock = new();
    private static readonly nint[] KeyArgument = new nint[1];
    private static IRuntimeMethod? _getKey;
    private static IRuntimeMethod? _getAnyKey;
    private static long _nextBindTicks;
    private static long _lastPollTicks;
    private static long _lastNativeEventTicks;
    private static bool _recording;
    private static bool _anyKey;

    internal static void Update(ReplayPlugin plugin)
    {
        if (!plugin.IsRecordingKeyboard)
        {
            if (Volatile.Read(ref _recording))
                Reset();
            return;
        }

        if (Volatile.Read(ref _recording) == false)
        {
            Volatile.Write(ref _recording, true);
            lock (StateLock)
            {
                PreviousDown.Clear();
                NativeDownKeys.Clear();
            }
            _lastPollTicks = 0;
        }

        long now = Environment.TickCount64;
        if (_lastPollTicks == now)
            return;
        _lastPollTicks = now;
        long lastNative = Volatile.Read(ref _lastNativeEventTicks);
        if (lastNative != 0 && now >= lastNative && now - lastNative < 100L)
            return;
        if (!TryBind(now))
        {
            PollImGui(plugin);
            return;
        }

        try
        {
            _anyKey = _getAnyKey != null && _getAnyKey.InvokeStaticUnbox<bool>();
            if (!_anyKey)
            {
                // Some manager builds deliver hardware keys to ImGui while
                // Unity.Input.anyKey remains false. Probe that path before
                // releasing the edge detector.
                if (!PollImGui(plugin))
                    ReleaseObservedKeys(plugin);
                return;
            }

            foreach (ImGuiKey key in ReplayKeyMap.CapturableKeys())
            {
                bool nativeDown;
                lock (StateLock)
                    nativeDown = NativeDownKeys.Contains(key);
                if (nativeDown)
                {
                    lock (StateLock)
                        PreviousDown[key] = true;
                    continue;
                }
                if (!ReplayKeyMap.TryMapUnityKeyCode(key, out int unityKeyCode))
                    continue;
                bool down = InvokeKey(unityKeyCode);
                bool wasDown;
                lock (StateLock)
                {
                    wasDown = PreviousDown.TryGetValue(key, out bool previous) && previous;
                    PreviousDown[key] = down;
                }
                if (down == wasDown)
                    continue;
                plugin.RecordKeyboardInput(
                    ReplayKeyMap.GetBindingName(key),
                    down ? 0 : 1,
                    0);
            }
        }
        catch (Exception exception)
        {
            ResetBinding($"Unity keyboard poll failed: {exception.Message}", now);
            PollImGui(plugin);
        }
    }

    internal static void CaptureNativeEvent(ReplayPlugin plugin, IntPtr inputEvent)
    {
        if (inputEvent == IntPtr.Zero || !plugin.IsRecordingKeyboard)
            return;
        try
        {
            if (AndroidInput.AInputEvent_getType(inputEvent) != AndroidInput.EventType.Key)
                return;
            int keyCode = AndroidInput.AKeyEvent_getKeyCode(inputEvent);
            if (!ReplayKeyMap.TryMapAndroidKeyCode(keyCode, out ImGuiKey key))
                return;
            AndroidInput.KeyAction action = AndroidInput.AKeyEvent_getAction(inputEvent);
            int repeat = Math.Max(0, AndroidInput.AKeyEvent_getRepeatCount(inputEvent));
            bool down = action is AndroidInput.KeyAction.Down or AndroidInput.KeyAction.Multiple;
            string binding = ReplayKeyMap.GetBindingName(key);
            if (binding.Length == 0)
                return;

            Volatile.Write(ref _recording, true);
            Volatile.Write(ref _lastNativeEventTicks, Environment.TickCount64);

            // Keep the polling edge detector in sync so a native event and its
            // Unity reflection snapshot cannot produce duplicate transitions.
            lock (StateLock)
            {
                if (down)
                    NativeDownKeys.Add(key);
                else
                    NativeDownKeys.Remove(key);
                PreviousDown[key] = down;
            }
            plugin.RecordKeyboardInput(binding, down ? 0 : 1, repeat);
        }
        catch (Exception exception)
        {
            Logger.Debug("Replay", "Native keyboard event read failed: " + exception.Message);
        }
    }

    internal static void Reset()
    {
        lock (StateLock)
        {
            PreviousDown.Clear();
            NativeDownKeys.Clear();
        }
        _getKey = null;
        _getAnyKey = null;
        _nextBindTicks = 0;
        _lastPollTicks = 0;
        Volatile.Write(ref _lastNativeEventTicks, 0L);
        Volatile.Write(ref _recording, false);
        _anyKey = false;
    }

    private static void ReleaseObservedKeys(ReplayPlugin plugin)
    {
        KeyValuePair<ImGuiKey, bool>[] observed;
        lock (StateLock)
            observed = PreviousDown.ToArray();
        if (observed.Length == 0)
            return;
        foreach (KeyValuePair<ImGuiKey, bool> pair in observed)
        {
            bool nativeDown;
            lock (StateLock)
                nativeDown = NativeDownKeys.Contains(pair.Key);
            if (!pair.Value || nativeDown)
                continue;
            lock (StateLock)
                PreviousDown[pair.Key] = false;
            plugin.RecordKeyboardInput(ReplayKeyMap.GetBindingName(pair.Key), 1, 0);
        }
    }

    private static bool PollImGui(ReplayPlugin plugin)
    {
        bool observedAny = false;
        foreach (ImGuiKey key in ReplayKeyMap.CapturableKeys())
        {
            bool nativeDown;
            lock (StateLock)
                nativeDown = NativeDownKeys.Contains(key);
            if (nativeDown)
            {
                lock (StateLock)
                    PreviousDown[key] = true;
                observedAny = true;
                continue;
            }
            bool down;
            try { down = ImGui.IsKeyDown(key); }
            catch { return false; }
            bool wasDown;
            lock (StateLock)
            {
                wasDown = PreviousDown.TryGetValue(key, out bool previous) && previous;
                PreviousDown[key] = down;
            }
            if (down)
                observedAny = true;
            if (down == wasDown)
                continue;
            plugin.RecordKeyboardInput(ReplayKeyMap.GetBindingName(key), down ? 0 : 1, 0);
        }
        bool previousAny;
        lock (StateLock)
            previousAny = PreviousDown.Values.Any(value => value);
        return observedAny || previousAny;
    }

    private static bool TryBind(long now)
    {
        if (_getKey != null && _getAnyKey != null)
            return true;
        if (now < _nextBindTicks)
            return false;
        _nextBindTicks = now + BindRetryMilliseconds;
        try
        {
            IAppDomain? domain = RuntimeManager.GetDomain();
            if (domain == null)
                return false;
            IRuntimeClass? inputClass = null;
            foreach (IRuntimeAssembly assembly in domain.GetAssemblies())
            {
                try
                {
                    inputClass = assembly.GetClass("UnityEngine", "Input");
                    if (inputClass != null) break;
                }
                catch { }
            }
            if (inputClass == null)
                return false;
            _getKey = inputClass.GetMethod("GetKeyInt", "UnityEngine.KeyCode")
                ?? inputClass.GetMethod("GetKeyInt", "KeyCode")
                ?? inputClass.GetMethod("GetKeyInt", 1)
                ?? inputClass.GetMethod("GetKey", "UnityEngine.KeyCode")
                ?? inputClass.GetMethod("GetKey", "KeyCode")
                ?? inputClass.GetMethod("GetKey", 1);
            _getAnyKey = inputClass.GetMethod("get_anyKey", 0);
            if (_getKey == null || _getAnyKey == null)
            {
                _getKey = null;
                _getAnyKey = null;
                return false;
            }
            return true;
        }
        catch
        {
            _getKey = null;
            _getAnyKey = null;
            return false;
        }
    }

    private static bool InvokeKey(int unityKeyCode)
    {
        int keyCode = unityKeyCode;
        KeyArgument[0] = (nint)(&keyCode);
        return _getKey!.InvokeStaticUnbox<bool>(KeyArgument);
    }

    private static void ResetBinding(string status, long now)
    {
        _getKey = null;
        _getAnyKey = null;
        _nextBindTicks = now + BindRetryMilliseconds;
        Volatile.Write(ref _lastNativeEventTicks, 0L);
        lock (StateLock)
        {
            PreviousDown.Clear();
            NativeDownKeys.Clear();
        }
        Logger.Debug("Replay", status);
    }
}
