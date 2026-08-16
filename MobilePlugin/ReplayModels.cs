using System.Text.Json.Serialization;

namespace Replay.Mobile;

public sealed class ReplayData
{
    public int FormatVersion { get; set; } = 2;
    public string ModVersion { get; set; } = ReplayPlugin.ModVersion;
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public string SongName { get; set; } = "Unknown";
    public string Title { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string LevelPath { get; set; } = "";
    public string SceneName { get; set; } = "";
    public string LevelId { get; set; } = "";
    public bool IsOfficialLevel { get; set; }
    public bool Completed { get; set; }
    public float Speed { get; set; } = 1f;
    public float Bpm { get; set; }
    public int StartTile { get; set; }
    public int EndTile { get; set; }
    public int TotalTiles { get; set; }
    public List<ReplayHit> Hits { get; set; } = new();
    public List<ReplayTouchInput> TouchEvents { get; set; } = new();
    public List<ReplayKeyboardInput> KeyboardEvents { get; set; } = new();
}

public sealed class ReplayHit
{
    public int SequenceId { get; set; }
    public double HitAngleOffset { get; set; }
    public int HitMargin { get; set; } = 3;
    public bool NoFailHit { get; set; }
    public bool AutoHit { get; set; }
}

/// <summary>
/// 与判定轨道独立的移动端触摸输入轨道。这里保存录制过程中收到的每个完整
/// InputEvents.OnTouch 快照；未产生游戏判定的触摸也会保留。
/// </summary>
public sealed class ReplayTouchInput
{
    public long TimeMilliseconds { get; set; }
    public int Action { get; set; }
    public int PointerId { get; set; } = -1;
    public float X { get; set; }
    public float Y { get; set; }
    public float SourceWidth { get; set; }
    public float SourceHeight { get; set; }
}

/// <summary>
/// 与判定轨道独立的硬件键盘输入轨道。Binding 使用 ImGui/Unity 兼容的规范化名称，
/// 避免把 Android keyCode 泄漏到跨设备的回放文件中。
/// </summary>
public sealed class ReplayKeyboardInput
{
    public long TimeMilliseconds { get; set; }
    public string Binding { get; set; } = "";
    /// <summary>0 = key down, 1 = key up。</summary>
    public int Action { get; set; }
    public int Repeat { get; set; }
}

internal sealed class ReplaySettings
{
    public int SettingsVersion { get; set; }
    public bool SaveFullClear { get; set; } = true;
    public bool SaveEveryCompletion { get; set; }
    public bool SaveEveryFailure { get; set; }
    public bool SaveFailureAt90Percent { get; set; } = true;
    public bool IgnoreAutoplay { get; set; } = true;
    public bool ShowReplayHud { get; set; } = true;
    public bool ReceiveTouchInput { get; set; } = true;
    // Hardware keyboard capture is opt-in so touch-only users never activate
    // its hook or polling fallback.
    public bool ReceiveKeyboardInput { get; set; }
    public int HudFontSize { get; set; } = 24;
    public float HudPositionX { get; set; } = 0.02f;
    public float HudPositionY { get; set; } = 0.08f;
    public int MaximumSavedReplays { get; set; } = 100;
    public string ReplayDirectory { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(ReplayData))]
[JsonSerializable(typeof(ReplayTouchInput))]
[JsonSerializable(typeof(ReplayKeyboardInput))]
[JsonSerializable(typeof(ReplaySettings))]
internal partial class ReplayJsonContext : JsonSerializerContext
{
}

internal readonly record struct PendingHit(
    int SequenceId,
    int HitIndex);

internal sealed record ReplayLevelIdentity(
    string SongName,
    string ArtistName,
    string LevelPath,
    string SceneName,
    string LevelId,
    bool IsOfficialLevel,
    int TotalTiles)
{
    internal string StableKey => IsOfficialLevel
        ? $"official:{LevelId}:{SceneName}:{SongName}:{TotalTiles}"
        : $"custom:{LevelPath}:{SongName}:{TotalTiles}";
}

internal enum ReplayRunState
{
    Idle,
    Loading,
    WaitingForStart,
    Playing,
    Paused,
    Finished,
    Failed,
}

internal enum ReplayLoadStage
{
    None,
    WaitingForCustomLevelBrowser,
    WaitingForLevelSelect,
    WaitingForTargetStart,
}

internal enum CustomReplayLoadStatus
{
    Waiting,
    Started,
    Failed,
}

internal enum ReplayCommandKind
{
    SaveCurrent,
    SaveResult,
    Play,
    Stop,
    TogglePause,
    PauseForManager,
    ResumeAfterManager,
}

internal sealed record ReplayCommand(ReplayCommandKind Kind, ReplayData? Replay = null);

internal sealed record ReplayFileEntry(
    string Path,
    string Title,
    string SongName,
    string ArtistName,
    DateTime RecordedAtUtc,
    int HitCount,
    int StartTile,
    int EndTile,
    int TotalTiles,
    float Speed,
    bool IsOfficialLevel,
    bool Completed,
    bool NativeFormat,
    bool Supported,
    string? Error)
{
    internal string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? SongName : Title;
}
