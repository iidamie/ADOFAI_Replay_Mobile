using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

/// <summary>
/// Captures a multipress damage/death decision. The game returns this result
/// from scrPlayer.OnDamage even when No Fail prevents the actual death.
/// </summary>
public static partial class ReplayJudgementHooks
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

            Logger.Info(LogTag, "Installed multipress judgement hook");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Debug(LogTag, $"Multipress judgement hook unavailable: {exception.Message}");
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
            Logger.Debug(LogTag, $"Multipress judgement hook unload failed: {exception.Message}");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "OnDamage", ParameterCount = 4)]
    private static byte PlayerOnDamage(
        nint instance,
        byte multipress,
        byte applyMultipressDamage,
        byte skipDamage,
        int hitMargin,
        nint methodInfo)
    {
        byte result = PlayerOnDamageOriginal(
            instance,
            multipress,
            applyMultipressDamage,
            skipDamage,
            hitMargin,
            methodInfo);

        if (multipress != 0 && result != 0)
            GameHooks.CaptureMultipressJudgement();
        return result;
    }
}
