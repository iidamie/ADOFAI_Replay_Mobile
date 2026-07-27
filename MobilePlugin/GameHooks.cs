using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace Replay.Mobile;

internal static class GameHooks
{
    private const string LogTag = "Replay";
    private static readonly List<HookRegistration> Registrations = new();

    private static ReplayPlugin? _plugin;
    private static PlayerHitDelegate? _playerHitOriginal;
    private static ValidInputDelegate? _validInputOriginal;
    private static GetHitMarginDelegate? _getHitMarginOriginal;
    private static StartRewindDelegate? _startRewindOriginal;
    private static UpdateDelegate? _controllerUpdateOriginal;
    private static UpdateDelegate? _conductorUpdateOriginal;
    private static FailActionDelegate? _failActionOriginal;
    private static BeatLevelDelegate? _beatLevelOriginal;
    private static OnLandOnPortalDelegate? _onLandOnPortalOriginal;
    private static SaveCustomDelegate? _saveCustomOriginal;
    private static nint _playerHitMethodInfo;
    private static nint _getHitMarginMethodInfo;
    private static bool _capturingHit;
    private static int _capturedMargin = 3;
    private static bool _capturedMarginAvailable;
    private static bool _injectingHit;
    private static int _injectedMargin = 3;

    internal static bool Install(ReplayPlugin plugin, GameApi game)
    {
        try
        {
            Uninstall();
            _plugin = plugin;

            _playerHitOriginal = InstallHook(game, "scrPlayer", "Hit", 1,
                (PlayerHitDelegate)PlayerHit, required: true, out _playerHitMethodInfo);
            _validInputOriginal = InstallHook(game, "scrPlayer", "ValidInputWasTriggered", 0,
                (ValidInputDelegate)ValidInput, required: true, out _);
            _getHitMarginOriginal = InstallHook(game, "scrMisc", "GetHitMargin", 6,
                (GetHitMarginDelegate)GetHitMargin, required: true, out _getHitMarginMethodInfo);
            _startRewindOriginal = InstallHook(game, "scrController", "Start_Rewind", 1,
                (StartRewindDelegate)StartRewind, required: true, out _);
            _controllerUpdateOriginal = InstallHook(game, "scrController", "Update", 0,
                (UpdateDelegate)ControllerUpdate, required: true, out _);
            _conductorUpdateOriginal = InstallHook(game, "scrConductor", "Update", 0,
                (UpdateDelegate)ConductorUpdate, required: true, out _);
            _failActionOriginal = InstallHook(game, "scrController", "FailAction", 4,
                (FailActionDelegate)FailAction, required: false, out _);
            _beatLevelOriginal = InstallHook(game, "scrController", "BeatLevel", 0,
                (BeatLevelDelegate)BeatLevel, required: false, out _);
            _onLandOnPortalOriginal = InstallHook(game, "scrController", "OnLandOnPortal", 3,
                (OnLandOnPortalDelegate)OnLandOnPortal, required: false, out _);
            _saveCustomOriginal = InstallHook(game, "scrMistakesManager", "SaveCustom", 3,
                (SaveCustomDelegate)SaveCustom, required: false, out _);
            if (_playerHitOriginal == null
                || _validInputOriginal == null
                || _getHitMarginOriginal == null
                || _startRewindOriginal == null
                || _controllerUpdateOriginal == null
                || _conductorUpdateOriginal == null)
            {
                Uninstall();
                return false;
            }

            Logger.Info(LogTag, $"Installed {Registrations.Count} IL2CPP hooks");
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
        _plugin = null;
        for (int index = Registrations.Count - 1; index >= 0; index--)
            HookHelper.Unhook(Registrations[index].Target);
        Registrations.Clear();

        _playerHitOriginal = null;
        _validInputOriginal = null;
        _getHitMarginOriginal = null;
        _startRewindOriginal = null;
        _controllerUpdateOriginal = null;
        _conductorUpdateOriginal = null;
        _failActionOriginal = null;
        _beatLevelOriginal = null;
        _onLandOnPortalOriginal = null;
        _saveCustomOriginal = null;
        _playerHitMethodInfo = 0;
        _getHitMarginMethodInfo = 0;
        _capturingHit = false;
        _capturedMarginAvailable = false;
        _injectingHit = false;
    }

    internal static byte InjectPlayerHit(nint player, bool autoHit, int hitMargin)
    {
        PlayerHitDelegate? original = _playerHitOriginal;
        if (original == null || player == 0)
            return 0;

        _injectingHit = true;
        _injectedMargin = hitMargin;
        try
        {
            return original(player, (byte)(autoHit ? 1 : 0), _playerHitMethodInfo);
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
        return _getHitMarginOriginal?.Invoke(
            angle,
            targetAngle,
            (byte)(clockwise ? 1 : 0),
            bpm,
            pitch,
            marginScale,
            _getHitMarginMethodInfo) ?? 3;
    }

    private static TDelegate? InstallHook<TDelegate>(
        GameApi game,
        string className,
        string methodName,
        int parameterCount,
        TDelegate detour,
        bool required,
        out nint methodInfo) where TDelegate : Delegate
    {
        methodInfo = 0;
        var method = game.ResolveMethod(className, methodName, parameterCount);
        if (method == null || method.FunctionPtr == 0)
        {
            LogInstallFailure(className, methodName, required, "method not found");
            return null;
        }

        methodInfo = method.Ptr;
        nint target = method.FunctionPtr;
        nint detourPointer = Marshal.GetFunctionPointerForDelegate(detour);
        nint originalPointer = HookHelper.Hook(target, detourPointer);
        if (originalPointer == 0)
        {
            LogInstallFailure(className, methodName, required, "Dobby returned a null trampoline");
            return null;
        }

        try
        {
            TDelegate original = Marshal.GetDelegateForFunctionPointer<TDelegate>(originalPointer);
            Registrations.Add(new HookRegistration(target, detour, original));
            return original;
        }
        catch (Exception exception)
        {
            HookHelper.Unhook(target);
            LogInstallFailure(className, methodName, required, exception.Message);
            return null;
        }
    }

    private static void LogInstallFailure(string className, string methodName, bool required, string reason)
    {
        string message = $"Hook {className}.{methodName} failed: {reason}";
        if (required)
            Logger.Error(LogTag, message);
        else
            Logger.Warn(LogTag, message);
    }

    private static byte PlayerHit(nint instance, byte autoHit, nint methodInfo)
    {
        ReplayPlugin? plugin = _plugin;
        PlayerHitDelegate? original = _playerHitOriginal;
        if (original == null)
            return 0;

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

        _capturingHit = pending.HasValue;
        _capturedMargin = 3;
        _capturedMarginAvailable = false;
        try
        {
            byte result = original(instance, autoHit, methodInfo);
            try
            {
                if (pending.HasValue)
                    plugin?.CompleteHit(
                        pending.Value,
                        _capturedMarginAvailable ? _capturedMargin : null);
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
            _capturingHit = false;
            _capturedMargin = 3;
            _capturedMarginAvailable = false;
        }
    }

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
        return _validInputOriginal?.Invoke(instance, methodInfo) ?? 0;
    }

    private static int GetHitMargin(
        float angle,
        float targetAngle,
        byte clockwise,
        float bpm,
        float pitch,
        double marginScale,
        nint methodInfo)
    {
        int result = _getHitMarginOriginal?.Invoke(
            angle,
            targetAngle,
            clockwise,
            bpm,
            pitch,
            marginScale,
            methodInfo) ?? 3;

        if (_injectingHit)
            return _injectedMargin;
        if (_capturingHit)
        {
            _capturedMargin = result;
            _capturedMarginAvailable = true;
        }
        return result;
    }

    private static void StartRewind(nint instance, int sequenceId, nint methodInfo)
    {
        ReplayPlugin? plugin = _plugin;
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
            _startRewindOriginal?.Invoke(instance, sequenceId, methodInfo);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Start_Rewind trampoline failed: {exception}");
            return;
        }

        try
        {
            plugin?.HandleStartRewind(instance, sequenceId);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Replay rewind handling failed: {exception}");
        }
    }

    private static void ControllerUpdate(nint instance, nint methodInfo)
    {
        _controllerUpdateOriginal?.Invoke(instance, methodInfo);
        try
        {
            _plugin?.TickMainThread(instance);
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"Controller update failed: {exception}");
        }
    }

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
        _conductorUpdateOriginal?.Invoke(instance, methodInfo);
    }

    private static void FailAction(
        nint instance,
        byte overload,
        byte showText,
        nint customText,
        byte useTransition,
        nint methodInfo)
    {
        bool replayResult = false;
        try
        {
            replayResult = _plugin?.HandleFail(instance) == true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"FailAction detour failed: {exception}");
        }
        try
        {
            _failActionOriginal?.Invoke(instance, overload, showText, customText, useTransition, methodInfo);
        }
        finally
        {
            if (replayResult)
                _plugin?.ReleaseReplayAfterResult("failure");
        }
    }

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
            _beatLevelOriginal?.Invoke(instance, methodInfo);
        }
        finally
        {
            if (replayResult)
                _plugin?.ReleaseReplayAfterResult("completion");
        }
    }

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
            _onLandOnPortalOriginal?.Invoke(instance, planet, portal, arguments, methodInfo);
        }
        finally
        {
            if (replayResult)
                _plugin?.ReleaseReplayAfterResult("completion");
        }
    }

    private static EndLevelInfo SaveCustom(
        nint instance,
        nint levelData,
        byte save,
        float speed,
        nint methodInfo)
    {
        try
        {
            if (_plugin?.IsReplayActive == true)
                return default;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"SaveCustom detour failed: {exception}");
        }
        return _saveCustomOriginal?.Invoke(instance, levelData, save, speed, methodInfo) ?? default;
    }

    private sealed record HookRegistration(nint Target, Delegate Detour, Delegate Original);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte PlayerHitDelegate(nint instance, byte autoHit, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ValidInputDelegate(nint instance, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetHitMarginDelegate(
        float angle,
        float targetAngle,
        byte clockwise,
        float bpm,
        float pitch,
        double marginScale,
        nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StartRewindDelegate(nint instance, int sequenceId, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateDelegate(nint instance, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FailActionDelegate(
        nint instance,
        byte overload,
        byte showText,
        nint customText,
        byte useTransition,
        nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BeatLevelDelegate(nint instance, nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnLandOnPortalDelegate(
        nint instance,
        nint planet,
        int portal,
        nint arguments,
        nint methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate EndLevelInfo SaveCustomDelegate(
        nint instance,
        nint levelData,
        byte save,
        float speed,
        nint methodInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct EndLevelInfo
    {
        internal int EndLevelType;
        internal int NewBestType;
    }
}
