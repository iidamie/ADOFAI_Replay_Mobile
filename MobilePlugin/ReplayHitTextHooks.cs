using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

/// <summary>
/// Captures final judgements such as Multipress and OverPress, which are
/// selected by scrPlanet.SwitchChosen after raw timing has been calculated.
/// </summary>
public static partial class ReplayHitTextHooks
{
    private const string LogTag = "Replay";

    internal static bool Install()
    {
        try
        {
            Uninstall();
            if (!InstallHooks())
            {
                Uninstall();
                return false;
            }

            Logger.Info(LogTag, "Installed final hit-text judgement hook");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, $"Final hit-text judgement hook unavailable: {exception.Message}");
            Uninstall();
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
            Logger.Debug(LogTag, $"Final hit-text judgement hook unload failed: {exception.Message}");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrHitTextManager", "ShowHitText", ParameterCount = 3)]
    private static void ShowHitText(
        nint instance,
        int hitMargin,
        nint planet,
        float missAngle,
        nint methodInfo)
    {
        ShowHitTextOriginal(instance, hitMargin, planet, missAngle, methodInfo);
        GameHooks.CaptureJudgement(hitMargin);
    }
}
