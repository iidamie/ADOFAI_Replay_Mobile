using StArray.ModManager.Manager;

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

    public static bool IsPlaybackActive => Volatile.Read(ref _playbackActive) != 0;

    /// <summary>回放开始或重新开始时触发，Viewer 应清空实体和虚拟输入状态。</summary>
    public static event Action? PlaybackStarted;

    /// <summary>回放停止、失败、通关或 Replay 卸载时触发。</summary>
    public static event Action? PlaybackEnded;

    /// <summary>
    /// 独立于 Android 实体输入通道的虚拟触摸事件。
    /// 参数依次为：MotionAction、指针 ID、X、Y、录制时宽度、录制时高度。
    /// </summary>
    public static event Action<int, int, float, float, float, float>? ReplayTouch;

    internal static void BeginPlayback()
    {
        Interlocked.Exchange(ref _playbackActive, 1);
        Dispatch(PlaybackStarted);
    }

    internal static void EndPlayback()
    {
        if (Interlocked.Exchange(ref _playbackActive, 0) == 0)
            return;
        Dispatch(PlaybackEnded);
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
}
