using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

/// <summary>Isolated scnEditor.ResetScene hook for ending editor recording.</summary>
public static partial class EditorResetHook
{
    private const string LogTag = "Replay";
    private static ReplayPlugin? _plugin;
    private static int _installed;

    internal static bool Install(ReplayPlugin plugin)
    {
        if (Volatile.Read(ref _installed) != 0)
        {
            _plugin = plugin;
            return true;
        }

        _plugin = plugin;
        try
        {
            if (!InstallHooks())
            {
                Uninstall();
                return false;
            }

            Volatile.Write(ref _installed, 1);
            return true;
        }
        catch (Exception exception)
        {
            Uninstall();
            Logger.Debug(LogTag, $"scnEditor.ResetScene Hook unavailable: {exception.Message}");
            return false;
        }
    }

    internal static void Uninstall()
    {
        try
        {
            UninstallHooks();
        }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, $"scnEditor.ResetScene Hook unload failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _installed, 0);
            _plugin = null;
        }
    }

    internal static void Detach()
        => _plugin = null;

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
