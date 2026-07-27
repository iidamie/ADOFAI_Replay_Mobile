using System.Formats.Nrbf;
using System.Reflection;

namespace Replay.Mobile;

internal static class LegacyReplayReader
{
    private const long MaximumReplaySize = 64L * 1024L * 1024L;

    internal static ReplayData Load(Stream stream)
    {
        if (stream.CanSeek && stream.Length > MaximumReplaySize)
            throw new InvalidDataException("PC 回放文件超过 64 MB 限制。");

        SerializationRecord decoded = NrbfDecoder.Decode(stream);
        if (decoded is not ClassRecord root
            || !root.TypeName.FullName.EndsWith("ReplayInfo", StringComparison.Ordinal))
            throw new InvalidDataException("不是受支持的 ADOFAI Replay 文件。");

        int startTile = ReadInt32(root, "StartTile");
        int endTile = ReadInt32(root, "EndTile");
        int allTile = ReadInt32(root, "AllTile");
        bool official = ReadBoolean(root, "IsOfficialLevel");
        string levelPath = ReadString(root, "Path");
        List<ReplayHit> hits = ReadHits(root);
        if (hits.Count == 0)
            throw new InvalidDataException("PC 回放中没有判定记录。");

        DateTime recordedAt = ReadDateTime(root, "Time");
        if (recordedAt == default)
            recordedAt = DateTime.UtcNow;
        else if (recordedAt.Kind != DateTimeKind.Utc)
            recordedAt = recordedAt.ToUniversalTime();

        return new ReplayData
        {
            FormatVersion = 1,
            ModVersion = "pc-import",
            SessionId = Guid.NewGuid().ToString("N"),
            RecordedAtUtc = recordedAt,
            SongName = ReadString(root, "SongName", "Unknown"),
            ArtistName = ReadString(root, "ArtistName"),
            LevelPath = official ? "" : levelPath,
            SceneName = official ? levelPath : "",
            LevelId = official ? SelectOfficialLevelId(levelPath, ReadString(root, "SongName")) : "",
            IsOfficialLevel = official,
            Completed = allTile > 0 && endTile >= allTile - 2,
            Speed = ReadSingle(root, "Speed", 1f),
            Bpm = ReadSingle(root, "BPM"),
            StartTile = Math.Max(0, startTile),
            EndTile = Math.Max(Math.Max(0, startTile), endTile),
            TotalTiles = Math.Max(allTile, endTile + 1),
            Hits = hits,
        };
    }

    private static string SelectOfficialLevelId(string path, string songName)
    {
        if (!string.IsNullOrWhiteSpace(path) && !path.StartsWith("scn", StringComparison.OrdinalIgnoreCase))
            return path;
        return songName;
    }

    private static List<ReplayHit> ReadHits(ClassRecord root)
    {
        ArrayRecord? arrayRecord = root.GetArrayRecord("Tiles");
        if (arrayRecord == null)
            return new List<ReplayHit>();

        MethodInfo? getArray = arrayRecord.GetType().GetMethod(
            "GetArray",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(bool) },
            null);
        if (getArray?.Invoke(arrayRecord, new object[] { true }) is not Array values)
            throw new InvalidDataException("无法读取 PC 回放的 Tiles 数组。");

        List<ReplayHit> hits = new(values.Length);
        foreach (object? value in values)
        {
            if (value is not ClassRecord tile)
                continue;
            hits.Add(new ReplayHit
            {
                SequenceId = ReadInt32(tile, "SeqID"),
                HitAngleOffset = ReadDouble(tile, "HitAngleRatio"),
                HitMargin = ReadEnum(tile, "Hitmargin", 3),
                NoFailHit = ReadBoolean(tile, "NoFailHit"),
                AutoHit = ReadBoolean(tile, "AutoHit"),
            });
        }
        return hits;
    }

    private static int ReadEnum(ClassRecord record, string name, int fallback)
    {
        object? raw = record.HasMember(name) ? record.GetRawValue(name) : null;
        if (raw is ClassRecord enumRecord && enumRecord.HasMember("value__"))
            return ReadInt32(enumRecord, "value__", fallback);
        try
        {
            return raw == null ? fallback : Convert.ToInt32(raw);
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadString(ClassRecord record, string name, string fallback = "")
    {
        if (!record.HasMember(name))
            return fallback;
        try
        {
            return record.GetString(name) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBoolean(ClassRecord record, string name, bool fallback = false)
    {
        if (!record.HasMember(name))
            return fallback;
        try
        {
            return record.GetBoolean(name);
        }
        catch
        {
            return fallback;
        }
    }

    private static int ReadInt32(ClassRecord record, string name, int fallback = 0)
    {
        if (!record.HasMember(name))
            return fallback;
        try
        {
            return record.GetInt32(name);
        }
        catch
        {
            return fallback;
        }
    }

    private static float ReadSingle(ClassRecord record, string name, float fallback = 0f)
    {
        if (!record.HasMember(name))
            return fallback;
        try
        {
            return record.GetSingle(name);
        }
        catch
        {
            return fallback;
        }
    }

    private static double ReadDouble(ClassRecord record, string name, double fallback = 0d)
    {
        if (!record.HasMember(name))
            return fallback;
        try
        {
            return record.GetDouble(name);
        }
        catch
        {
            return fallback;
        }
    }

    private static DateTime ReadDateTime(ClassRecord record, string name)
    {
        if (!record.HasMember(name))
            return default;
        try
        {
            return record.GetDateTime(name);
        }
        catch
        {
            return default;
        }
    }
}
