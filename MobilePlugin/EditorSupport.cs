using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

public static partial class EditorSupport
{
    private const string LogTag = "Replay";
    private static ReplayPlugin? _plugin;

    internal static void Install(ReplayPlugin plugin)
    {
        _plugin = plugin;
        if (InstallHooks())
        {
            Logger.Info(LogTag, "Installed editor-mode hooks");
            return;
        }
        UninstallHooks();
        Logger.Info(LogTag, "Editor-mode hooks are unavailable (scnEditor class or methods missing)");
    }

    internal static void Uninstall()
    {
        UninstallHooks();
        _plugin = null;
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scnEditor", "Play", ParameterCount = 0)]
    private static void EditorPlay(nint instance, nint methodInfo)
    {
        try
        {
            _plugin?.HandleEditorPlay(instance);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"EditorPlay hook failed: {exception}");
        }
        EditorPlayOriginal(instance, methodInfo);
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scnEditor", "ResetScene", ParameterCount = 1)]
    private static void EditorResetScene(nint instance, byte param, nint methodInfo)
    {
        try
        {
            _plugin?.HandleEditorReset();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"EditorResetScene hook failed: {exception}");
        }
        EditorResetSceneOriginal(instance, param, methodInfo);
    }
}
