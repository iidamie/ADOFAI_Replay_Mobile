using System.Text.Json.Serialization;

namespace Replay.Mobile;

public sealed class ReplayData
{
    public int FormatVersion { get; set; } = 1;
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
}

public sealed class ReplayHit
{
    public int SequenceId { get; set; }
    public double HitAngleOffset { get; set; }
    public int HitMargin { get; set; } = 3;
    public bool NoFailHit { get; set; }
    public bool AutoHit { get; set; }
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
    public int HudFontSize { get; set; } = 24;
    public float HudPositionX { get; set; } = 0.02f;
    public float HudPositionY { get; set; } = 0.08f;
    public int MaximumSavedReplays { get; set; } = 100;
    public string ReplayDirectory { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(ReplayData))]
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
