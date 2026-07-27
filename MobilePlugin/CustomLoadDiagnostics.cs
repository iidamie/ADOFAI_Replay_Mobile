using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace Replay.Mobile;

public static partial class CustomLoadDiagnostics
{
    private const string LogTag = "Replay";
    private static ReplayPlugin? _plugin;

    internal static void Install(ReplayPlugin plugin)
    {
        _plugin = plugin;
        if (InstallHooks())
        {
            Logger.Info(LogTag, "Installed custom-level load diagnostics");
            return;
        }
        UninstallHooks();
        Logger.Warn(LogTag, "Custom-level load diagnostics are unavailable");
    }

    internal static void Uninstall()
    {
        UninstallHooks();
        _plugin = null;
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scnGame", "LoadAndPlayLevel", ParameterCount = 1)]
    private static byte ScnGameLoadAndPlayLevel(nint instance, nint levelPath, nint methodInfo)
    {
        bool log = _plugin?.IsReplayLoadPending == true;
        if (log)
            LogPath("scnGame.LoadAndPlayLevel entered", levelPath);
        byte result = ScnGameLoadAndPlayLevelOriginal(instance, levelPath, methodInfo);
        if (log)
            Logger.Info(LogTag, $"scnGame.LoadAndPlayLevel returned: success={result != 0}");
        return result;
    }

    [UnmanagedHook("Assembly-CSharp.dll", "ADOFAI", "LevelData", "LoadLevel", ParameterCount = 2)]
    private static byte LevelDataLoadLevel(nint instance, nint levelPath, nint status, nint methodInfo)
    {
        bool log = _plugin?.IsReplayLoadPending == true;
        if (log)
            LogPath("ADOFAI.LevelData.LoadLevel entered", levelPath);
        byte result = LevelDataLoadLevelOriginal(instance, levelPath, status, methodInfo);
        if (log)
            Logger.Info(LogTag, $"ADOFAI.LevelData.LoadLevel returned: success={result != 0}");
        return result;
    }

    private static void LogPath(string stage, nint levelPath)
    {
        string path;
        try
        {
            path = levelPath == 0 ? "" : new RuntimeString(levelPath).ToString();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"{stage}: invalid path pointer 0x{levelPath:X}: {exception.Message}");
            return;
        }
        Logger.Info(LogTag, $"{stage}: path='{path}', exists={File.Exists(path)}");
    }
}
