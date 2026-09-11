using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

/// <summary>
/// Secondary editor-stop event. Some mobile builds use SwitchToEditMode
/// instead of ResetScene when leaving editor play mode.
/// </summary>
public static partial class EditorSwitchToEditModeHook
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
            Logger.Debug(LogTag, $"scnEditor.SwitchToEditMode Hook unavailable: {exception.Message}");
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
            Logger.Debug(LogTag, $"scnEditor.SwitchToEditMode Hook unload failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _installed, 0);
            _plugin = null;
        }
    }

    internal static void Detach()
        => _plugin = null;

    [UnmanagedHook("Assembly-CSharp.dll", "scnEditor", "SwitchToEditMode", ParameterCount = 1)]
    private static void SwitchToEditMode(nint instance, byte clsToEditor, nint methodInfo)
    {
        try
        {
            _plugin?.HandleEditorReset();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"SwitchToEditMode hook failed: {exception}");
        }
        SwitchToEditModeOriginal(instance, clsToEditor, methodInfo);
    }
}
