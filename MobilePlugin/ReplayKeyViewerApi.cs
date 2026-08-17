using StArray.ModManager.Manager;
using StArray.ModManager.Interop;

namespace Replay.Mobile;

/// <summary>
/// Replay 与移动端按键查看器之间的可选私有协议。
///
/// Viewer 不引用 Replay 程序集，而是在运行时按这个类型名绑定事件，因此 Replay、
/// YoonKeyViewer、JipperKeyViewer 可以独立安装、加载和卸载。
/// </summary>
public static class ReplayKeyViewerApi
{
    private static int _playbackActive;
    private static int _faultLogged;
    private static VirtualInputPlaybackPublisher? _v2Publisher;
    private static VirtualInputPlaybackSession? _v2Session;

    public static bool IsPlaybackActive => Volatile.Read(ref _playbackActive) != 0;
    public static bool IsVirtualInputV2Active
        => Volatile.Read(ref _v2Session) is { IsActive: true };

    /// <summary>回放开始或重新开始时触发，Viewer 应清空实体和虚拟输入状态。</summary>
    public static event Action? PlaybackStarted;

    /// <summary>回放停止、失败、通关或 Replay 卸载时触发。</summary>
    public static event Action? PlaybackEnded;

    /// <summary>
    /// 独立于 Android 实体输入通道的虚拟触摸事件。
    /// 参数依次为：MotionAction、指针 ID、X、Y、录制时宽度、录制时高度。
    /// </summary>
    public static event Action<int, int, float, float, float, float>? ReplayTouch;

    /// <summary>
    /// 独立于 Android 实体输入通道的虚拟键盘事件。
    /// 参数依次为：规范化绑定名、动作（0 down / 1 up）、重复次数。
    /// </summary>
    public static event Action<string, int, int>? ReplayKeyboard;

    internal static void BeginPlayback()
    {
        Interlocked.Exchange(ref _playbackActive, 1);
        EnsureV2Publisher();
        if (_v2Publisher != null &&
            !_v2Publisher.TryStart(out _v2Session, out var error))
            LogOnce("VirtualInput V2 start failed: " + error);
        Dispatch(PlaybackStarted);
    }

    internal static void EndPlayback()
    {
        if (Interlocked.Exchange(ref _playbackActive, 0) == 0)
            return;
        Interlocked.Exchange(ref _v2Session, null)?.Complete();
        Dispatch(PlaybackEnded);
    }

    internal static void InitializeV2() => EnsureV2Publisher();

    internal static void ShutdownV2()
    {
        Interlocked.Exchange(ref _v2Session, null)?.Cancel();
        Interlocked.Exchange(ref _v2Publisher, null)?.Dispose();
    }

    internal static bool TryCreateTouchV2(
        long timeMilliseconds,
        int action,
        int pointerId,
        float x,
        float y,
        float sourceWidth,
        float sourceHeight,
        out VirtualInputEvent input)
    {
        var phase = action switch
        {
            0 or 5 => VirtualInputPhase.Down,
            1 or 6 => VirtualInputPhase.Up,
            2 => VirtualInputPhase.Move,
            3 => VirtualInputPhase.Cancel,
            _ => (VirtualInputPhase)0
        };
        if (phase == 0)
        {
            input = default;
            return false;
        }
        input = new VirtualInputEvent(
            0,
            ToMicroseconds(timeMilliseconds),
            VirtualInputDevice.Touch,
            phase,
            null,
            pointerId,
            0,
            x,
            y,
            sourceWidth,
            sourceHeight);
        return true;
    }

    internal static bool TryCreateKeyboardV2(
        long timeMilliseconds,
        string binding,
        int action,
        int repeat,
        out VirtualInputEvent input)
    {
        if (string.IsNullOrWhiteSpace(binding) || action is not (0 or 1))
        {
            input = default;
            return false;
        }
        input = new VirtualInputEvent(
            0,
            ToMicroseconds(timeMilliseconds),
            VirtualInputDevice.Keyboard,
            action == 0 ? VirtualInputPhase.Down : VirtualInputPhase.Up,
            binding.Trim(),
            -1,
            Math.Max(0, repeat),
            0,
            0,
            0,
            0);
        return true;
    }

    internal static void PublishV2(IReadOnlyList<VirtualInputEvent> events)
    {
        var session = Volatile.Read(ref _v2Session);
        if (session == null || !session.IsActive || events.Count == 0)
            return;
        for (var offset = 0; offset < events.Count; offset += ModInteropConstants.VirtualInputMaxBatch)
        {
            var count = Math.Min(ModInteropConstants.VirtualInputMaxBatch, events.Count - offset);
            var batch = new VirtualInputEvent[count];
            for (var index = 0; index < count; ++index)
                batch[index] = events[offset + index];
            if (!session.TryPublish(batch, out var error))
            {
                LogOnce("VirtualInput V2 publish failed: " + error);
                return;
            }
        }
    }

    internal static void PublishTouch(
        int action,
        int pointerId,
        float x,
        float y,
        float sourceWidth,
        float sourceHeight)
    {
        if (!IsPlaybackActive)
            return;
        Action<int, int, float, float, float, float>? handlers = ReplayTouch;
        if (handlers == null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<int, int, float, float, float, float>)handler)(
                    action, pointerId, x, y, sourceWidth, sourceHeight);
            }
            catch (Exception exception)
            {
                LogOnce($"Replay touch subscriber threw: {exception.Message}");
            }
        }
    }

    internal static void PublishKeyboard(string binding, int action, int repeat)
    {
        if (!IsPlaybackActive || string.IsNullOrWhiteSpace(binding))
            return;
        Action<string, int, int>? handlers = ReplayKeyboard;
        if (handlers == null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string, int, int>)handler)(binding, action, repeat);
            }
            catch (Exception exception)
            {
                LogOnce($"Replay keyboard subscriber threw: {exception.Message}");
            }
        }
    }

    private static void Dispatch(Action? handlers)
    {
        if (handlers == null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception exception)
            {
                LogOnce($"Replay lifecycle subscriber threw: {exception.Message}");
            }
        }
    }

    private static void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _faultLogged, 1) != 0)
            return;
        try { Logger.Warn("ReplayKeyViewerApi", message); }
        catch { }
    }

    private static void EnsureV2Publisher()
    {
        if (_v2Publisher is { IsRetired: false })
            return;
        if (!ModInterop.TryOpenVirtualInputPlayback(out var publisher, out var error))
        {
            LogOnce("VirtualInput V2 unavailable: " + error);
            return;
        }
        _v2Publisher = publisher;
    }

    private static long ToMicroseconds(long milliseconds)
        => milliseconds <= 0
            ? 0
            : milliseconds >= long.MaxValue / 1_000L
                ? long.MaxValue
                : milliseconds * 1_000L;
}
