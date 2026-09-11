using StArray.ModManager.Manager;

namespace Replay.Mobile;

public static class EditorSupport
{
    private const string LogTag = "Replay";

    internal static void Install(ReplayPlugin plugin)
    {
        bool playInstalled = EditorPlayHook.Install(plugin);
        bool resetInstalled = EditorResetHook.Install(plugin);
        bool switchInstalled = EditorSwitchToEditModeHook.Install(plugin);
        Logger.Info(
            LogTag,
            $"Editor hooks: Play={playInstalled}, ResetScene={resetInstalled}, "
            + $"SwitchToEditMode={switchInstalled}");
    }

    internal static void Uninstall()
    {
        EditorPlayHook.Uninstall();
        EditorResetHook.Uninstall();
        EditorSwitchToEditModeHook.Uninstall();
    }

    internal static void Detach()
    {
        EditorPlayHook.Detach();
        EditorResetHook.Detach();
        EditorSwitchToEditModeHook.Detach();
    }
}
