using System.Runtime.InteropServices;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;

namespace Replay.Mobile;

public static partial class GameHooks
{
    private const string LogTag = "Replay";
    private const int HookCount = 10;
    private const int FailOverloadHitMargin = 9;

    private static ReplayPlugin? _plugin;
    private static nint _playerHitMethodInfo;
    private static nint _getHitMarginMethodInfo;
    [ThreadStatic]
    private static HitCapture? _hitCapture;
    [ThreadStatic]
    private static bool _injectingHit;
    private static int _injectedMargin = 3;

    private sealed class HitCapture
    {
        internal HitCapture(PendingHit pending)
        {
            Pending = pending;
        }

        internal PendingHit Pending { get; }
        internal int Margin { get; set; } = 3;
        internal bool MarginAvailable { get; set; }
        internal int MarginPriority { get; set; }
    }

    internal static bool Install(ReplayPlugin plugin, GameApi game)
    {
        try
        {
            Uninstall();
            _plugin = plugin;

            _playerHitMethodInfo = game.ResolveMethod("scrPlayer", "Hit", 1)?.Ptr ?? 0;
            _getHitMarginMethodInfo = game.ResolveMethod("scrMisc", "GetHitMargin", 6)?.Ptr ?? 0;
            if (_playerHitMethodInfo == 0 || _getHitMarginMethodInfo == 0)
            {
                Logger.Error(LogTag, "Required Replay method metadata was not found");
                Uninstall();
                return false;
            }
            if (!InstallHooks())
            {
                Logger.Error(LogTag, "One or more generated Replay hooks could not be installed");
                Uninstall();
                return false;
            }

            if (!ReplayJudgementHooks.Install())
                Logger.Warn(LogTag, "Multipress judgement hook unavailable; replay will use raw hit margins");
            if (!ReplayHitTextHooks.Install())
                Logger.Warn(LogTag, "Final hit-text judgement hook unavailable; replay will use raw hit margins");

            Logger.Info(LogTag, $"Installed {HookCount} generated IL2CPP hooks");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Hook installation failed: {exception}");
            Uninstall();
            return false;
        }
    }

    internal static void Uninstall()
    {
        ReplayJudgementHooks.Uninstall();
        ReplayHitTextHooks.Uninstall();
        UninstallHooks();
        _plugin = null;
        _playerHitMethodInfo = 0;
        _getHitMarginMethodInfo = 0;
        _hitCapture = null;
        _injectingHit = false;
    }

    internal static byte InjectPlayerHit(nint player, bool autoHit, int hitMargin)
    {
        if (player == 0 || _playerHitMethodInfo == 0)
            return 0;

        _injectingHit = true;
        _injectedMargin = hitMargin;
        try
        {
            return PlayerHitOriginal(player, (byte)(autoHit ? 1 : 0), _playerHitMethodInfo);
        }
        finally
        {
            _injectingHit = false;
            _injectedMargin = 3;
        }
    }

    internal static int CalculateHitMargin(
        float angle,
        float targetAngle,
        bool clockwise,
        float bpm,
        float pitch,
        double marginScale)
    {
        return GetHitMarginOriginal(
            angle,
            targetAngle,
            (byte)(clockwise ? 1 : 0),
            bpm,
            pitch,
            marginScale,
            _getHitMarginMethodInfo);
    }

    /// <summary>
    /// Captures the final judgement associated with the current input. The
    /// raw GetHitMargin result only describes timing; multipress, fail-overload
    /// and overpress can be decided later in the same game call.
    /// </summary>
    internal static void CaptureJudgement(int hitMargin)
    {
        if ((uint)hitMargin > 11u)
            return;

        HitCapture? capture = _hitCapture;
        if (capture == null)
            return;

        int priority = hitMargin == FailOverloadHitMargin
            ? 2
            : hitMargin is 7 or 8 or 10 or 11
                ? 1
                : 0;
        // A multipress may call OnDamage/Die first and then display the less
        // specific Multipress text. Preserve the stronger FailOverload result.
        if (priority < capture.MarginPriority)
            return;

        capture.Margin = hitMargin;
        capture.MarginAvailable = true;
        capture.MarginPriority = priority;
        _plugin?.CompleteHit(capture.Pending, hitMargin);
    }

    internal static void CaptureMultipressJudgement()
    {
        CaptureJudgement(FailOverloadHitMargin);
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "Hit", ParameterCount = 1)]
    private static byte PlayerHit(nint instance, byte autoHit, nint methodInfo)
    {
        ReplayPlugin? plugin = _plugin;
        PendingHit? pending = null;
        try
        {
            if (plugin?.ShouldBlockPlayerHit(instance) == true)
                return 0;
            pending = plugin?.BeginHit(instance, autoHit != 0);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Hit capture failed: {exception}");
        }

        HitCapture? previousCapture = _hitCapture;
        HitCapture? capture = pending.HasValue
            ? new HitCapture(pending.Value)
            : null;
        _hitCapture = capture;
        try
        {
            byte result = PlayerHitOriginal(instance, autoHit, methodInfo);
            try
            {
                if (pending.HasValue)
                    plugin?.CompleteHit(
                        pending.Value,
                        capture?.MarginAvailable == true ? capture.Margin : null);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"Hit commit failed: {exception}");
            }
            return result;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"scrPlayer.Hit trampoline failed: {exception}");
            return 0;
        }
        finally
        {
            _hitCapture = previousCapture;
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrPlayer", "ValidInputWasTriggered", ParameterCount = 0)]
    private static byte ValidInput(nint instance, nint methodInfo)
    {
        try
        {
            if (_plugin?.ShouldBlockInput() == true)
                return 0;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"ValidInput detour failed: {exception}");
        }
        return ValidInputOriginal(instance, methodInfo);
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrMisc", "GetHitMargin", ParameterCount = 6)]
    private static int GetHitMargin(
        float angle,
        float targetAngle,
        byte clockwise,
        float bpm,
        float pitch,
        double marginScale,
        nint methodInfo)
    {
        int result = GetHitMarginOriginal(
            angle,
            targetAngle,
            clockwise,
            bpm,
            pitch,
            marginScale,
            methodInfo);

        if (_injectingHit)
            return _injectedMargin;
        HitCapture? capture = _hitCapture;
        if (capture != null)
        {
            capture.Margin = result;
            capture.MarginAvailable = true;
        }
        return result;
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "Start_Rewind", ParameterCount = 1)]
    private static void StartRewind(nint instance, int sequenceId, nint methodInfo)
    {
        ReplayPlugin? plugin = _plugin;
        bool pendingReplay = plugin?.IsReplayTargetStartPending == true;
        if (pendingReplay)
            Logger.Info(LogTag, $"Replay target Start_Rewind entered: tile {sequenceId}");
        try
        {
            sequenceId = plugin?.GetStartTile(sequenceId) ?? sequenceId;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Replay start-tile selection failed: {exception}");
        }

        try
        {
            StartRewindOriginal(instance, sequenceId, methodInfo);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Start_Rewind trampoline failed: {exception}");
            return;
        }
        if (pendingReplay)
            Logger.Info(LogTag, "Replay target Start_Rewind original completed");

        try
        {
            plugin?.HandleStartRewind(instance, sequenceId);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Replay rewind handling failed: {exception}");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "Update", ParameterCount = 0)]
    private static void ControllerUpdate(nint instance, nint methodInfo)
    {
        ControllerUpdateOriginal(instance, methodInfo);
        try
        {
            _plugin?.TickMainThread(instance);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Controller update failed: {exception}");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrConductor", "Update", ParameterCount = 0)]
    private static void ConductorUpdate(nint instance, nint methodInfo)
    {
        try
        {
            _plugin?.TickConductorMainThread();
            _plugin?.TickPlayback();
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Replay update failed: {exception}");
        }
        ConductorUpdateOriginal(instance, methodInfo);
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "FailAction", ParameterCount = 4)]
    private static void FailAction(
        nint instance,
        byte overload,
        byte multipress,
        nint failMessage,
        byte hitbox,
        nint methodInfo)
    {
        bool replayResult = false;
        try
        {
            // HandleFail can finalize the current attempt. Commit the special
            // judgement before that cleanup happens.
            if (overload != 0 || multipress != 0)
                CaptureMultipressJudgement();
            replayResult = _plugin?.HandleFail(instance) == true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"FailAction detour failed: {exception}");
        }
        try
        {
            FailActionOriginal(instance, overload, multipress, failMessage, hitbox, methodInfo);
        }
        finally
        {
            if (replayResult)
                _plugin?.ReleaseReplayAfterResult("failure");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "BeatLevel", ParameterCount = 0)]
    private static void BeatLevel(nint instance, nint methodInfo)
    {
        bool replayResult = false;
        try
        {
            replayResult = _plugin?.HandleLevelComplete(instance) == true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"BeatLevel detour failed: {exception}");
        }
        try
        {
            BeatLevelOriginal(instance, methodInfo);
        }
        finally
        {
            if (replayResult)
                _plugin?.ReleaseReplayAfterResult("completion");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrController", "OnLandOnPortal", ParameterCount = 3)]
    private static void OnLandOnPortal(
        nint instance,
        nint planet,
        int portal,
        nint arguments,
        nint methodInfo)
    {
        bool replayResult = false;
        try
        {
            replayResult = _plugin?.HandleLevelComplete(instance) == true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"OnLandOnPortal detour failed: {exception}");
        }
        try
        {
            OnLandOnPortalOriginal(instance, planet, portal, arguments, methodInfo);
        }
        finally
        {
            if (replayResult)
                _plugin?.ReleaseReplayAfterResult("completion");
        }
    }

    [UnmanagedHook("Assembly-CSharp.dll", "scrMistakesManager", "SaveCustom", ParameterCount = 3)]
    private static EndLevelInfo SaveCustom(
        nint instance,
        nint levelHash,
        byte wonLevel,
        float speed,
        nint methodInfo)
    {
        ReplayPlugin? plugin = _plugin;
        bool replayResult = false;
        try
        {
            // Custom levels persist their result through SaveCustom. On some game builds this is
            // the only reliable completion callback, so use wonLevel as an autosave fallback.
            if (wonLevel != 0)
                replayResult = plugin?.HandleCustomLevelComplete() == true;
            if (replayResult || plugin?.IsReplayActive == true)
            {
                if (replayResult)
                    plugin?.ReleaseReplayAfterResult("custom completion");
                return default;
            }
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"SaveCustom detour failed: {exception}");
        }
        return SaveCustomOriginal(instance, levelHash, wonLevel, speed, methodInfo);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndLevelInfo
    {
        internal int EndLevelType;
        internal int NewBestType;
    }
}
