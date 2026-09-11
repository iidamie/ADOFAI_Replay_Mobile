using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

/// <summary>
/// Isolated scnEditor.Play hook. Keeping it separate from ResetScene prevents
/// one unavailable editor symbol from disabling the start event.
/// </summary>
public static partial class EditorPlayHook
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
            Logger.Debug(LogTag, $"scnEditor.Play Hook unavailable: {exception.Message}");
            return false;
        }
    }

    internal static void Uninstall()
    {
        try
        {
            // InstallHooks can leave earlier hooks installed when a later
            // symbol is unavailable, so always call the generated cleanup.
            UninstallHooks();
        }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, $"scnEditor.Play Hook unload failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _installed, 0);
            _plugin = null;
        }
    }

    internal static void Detach()
        => _plugin = null;

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
}
