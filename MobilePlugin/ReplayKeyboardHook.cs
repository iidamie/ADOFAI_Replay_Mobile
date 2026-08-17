using System.Runtime.InteropServices;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

public static partial class ReplayKeyboardHook
{
    private static ReplayPlugin? _plugin;
    private static bool _installed;
    private static long _nextAttemptTicks;

    internal static bool Install(ReplayPlugin plugin)
    {
        if (_installed)
            return true;
        long now = Environment.TickCount64;
        if (now < _nextAttemptTicks)
            return false;
        _nextAttemptTicks = now + 1000L;
        _plugin = plugin;
        try
        {
            _installed = InstallHooks();
            Logger.Info("Replay", _installed
                ? "Keyboard recording native hook installed"
                : "Keyboard recording native hook unavailable; Unity polling fallback remains active");
            if (!_installed)
                _plugin = null;
            return _installed;
        }
        catch (Exception exception)
        {
            _plugin = null;
            Logger.Debug("Replay", "Keyboard native hook installation deferred: " + exception.Message);
            return false;
        }
    }

    internal static void Uninstall()
    {
        if (!_installed)
        {
            _plugin = null;
            _nextAttemptTicks = 0;
            return;
        }
        try { UninstallHooks(); }
        catch (Exception exception) { Logger.Debug("Replay", "Keyboard hook unload failed: " + exception.Message); }
        finally
        {
            _installed = false;
            _plugin = null;
            _nextAttemptTicks = 0;
        }
    }

    [NativeHook(
        "libinput.so",
        "_ZN7android13InputConsumer18initializeKeyEventEPNS_8KeyEventEPKNS_12InputMessageE",
        Convention = CallingConvention.Cdecl)]
    private static unsafe void OnInitializeKeyEvent(void* consumer, void* keyEvent, void* message)
    {
        try
        {
            OnInitializeKeyEventOriginal(consumer, keyEvent, message);
        }
        finally
        {
            ReplayPlugin? plugin = _plugin;
            if (plugin != null && keyEvent != null)
                ReplayKeyboardRecorder.CaptureNativeEvent(plugin, new IntPtr(keyEvent));
        }
    }
}
