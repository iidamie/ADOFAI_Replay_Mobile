using System.Text;
using System.Text.Json;

namespace Replay.Mobile;

internal sealed class ReplayStore
{
    private readonly string _modDirectory;

    internal ReplayStore(string modDirectory)
    {
        _modDirectory = modDirectory;
    }

    internal string ResolveDirectory(string configuredPath)
    {
        string path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(_modDirectory, "Replays")
            : configuredPath.Trim();
        return Path.GetFullPath(path);
    }

    internal ReplaySettings LoadSettings()
    {
        string path = Path.Combine(_modDirectory, "replay_settings.json");
        if (!File.Exists(path))
            return new ReplaySettings();

        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ReplayJsonContext.Default.ReplaySettings)
            ?? new ReplaySettings();
    }

    internal void SaveSettings(ReplaySettings settings)
    {
        Directory.CreateDirectory(_modDirectory);
        string path = Path.Combine(_modDirectory, "replay_settings.json");
        string json = JsonSerializer.Serialize(settings, ReplayJsonContext.Default.ReplaySettings);
        WriteAtomic(path, json);
    }

    internal string Save(ReplayData replay, string configuredPath)
    {
        Validate(replay);
        string directory = ResolveDirectory(configuredPath);
        Directory.CreateDirectory(directory);

        string timestamp = replay.RecordedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        string song = SanitizeFileName(replay.SongName);
        string fileName = $"{timestamp}_{song}_{replay.SessionId[..Math.Min(8, replay.SessionId.Length)]}.rpl";
        string path = Path.Combine(directory, fileName);
        string temporaryPath = path + ".tmp";

        string json = JsonSerializer.Serialize(replay, ReplayJsonContext.Default.ReplayData);
        WriteAtomic(path, json, temporaryPath);
        return path;
    }

    internal ReplayData Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        int first = ReadFirstContentByte(stream);
        if (first != '{')
        {
            stream.Position = 0;
            return LegacyReplayReader.Load(stream);
        }

        stream.Position = 0;
        ReplayData? replay = JsonSerializer.Deserialize(stream, ReplayJsonContext.Default.ReplayData);
        if (replay == null)
            throw new InvalidDataException("回放文件为空。");
        Validate(replay);
        return replay;
    }

    internal List<ReplayFileEntry> Scan(string configuredPath)
    {
        string directory = ResolveDirectory(configuredPath);
        if (!Directory.Exists(directory))
            return new List<ReplayFileEntry>();

        List<ReplayFileEntry> entries = new();
        foreach (string path in Directory.EnumerateFiles(directory, "*.rpl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                bool nativeFormat = IsNativeFormat(path);
                ReplayData replay = Load(path);
                entries.Add(new ReplayFileEntry(
                    path,
                    replay.SongName,
                    replay.ArtistName,
                    replay.RecordedAtUtc,
                    replay.Hits.Count,
                    replay.StartTile,
                    replay.EndTile,
                    replay.TotalTiles,
                    replay.Speed,
                    replay.IsOfficialLevel,
                    replay.Completed,
                    nativeFormat,
                    true,
                    null));
            }
            catch (Exception exception)
            {
                entries.Add(new ReplayFileEntry(
                    path,
                    Path.GetFileNameWithoutExtension(path),
                    "",
                    File.GetLastWriteTimeUtc(path),
                    0,
                    0,
                    0,
                    0,
                    1f,
                    false,
                    false,
                    false,
                    false,
                    exception.Message));
            }
        }

        entries.Sort((left, right) => right.RecordedAtUtc.CompareTo(left.RecordedAtUtc));
        return entries;
    }

    internal void Trim(string configuredPath, int maximumCount)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 1000);
        List<ReplayFileEntry> mobileEntries = Scan(configuredPath)
            .Where(entry => entry.Supported && entry.NativeFormat)
            .ToList();
        for (int index = maximumCount; index < mobileEntries.Count; index++)
            File.Delete(mobileEntries[index].Path);
    }

    private static int ReadFirstContentByte(Stream stream)
    {
        int value;
        do
        {
            value = stream.ReadByte();
        } while (value >= 0 && char.IsWhiteSpace((char)value));
        return value;
    }

    private static bool IsNativeFormat(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return ReadFirstContentByte(stream) == '{';
    }

    private static void Validate(ReplayData replay)
    {
        if (replay.FormatVersion != 1)
            throw new InvalidDataException($"不支持回放格式版本 {replay.FormatVersion}。");
        replay.Hits ??= new List<ReplayHit>();
        replay.SongName = string.IsNullOrWhiteSpace(replay.SongName) ? "Unknown" : replay.SongName.Trim();
        replay.ArtistName ??= "";
        replay.LevelPath ??= "";
        replay.SceneName ??= "";
        replay.LevelId ??= "";
        replay.SessionId = string.IsNullOrWhiteSpace(replay.SessionId)
            ? Guid.NewGuid().ToString("N")
            : replay.SessionId;
        if (replay.StartTile < 0 || replay.EndTile < replay.StartTile)
            throw new InvalidDataException("回放瓦片范围无效。");
        if (replay.TotalTiles <= 0)
            replay.TotalTiles = Math.Max(replay.EndTile + 1, replay.StartTile + 1);
        if (replay.Hits.Count == 0)
            throw new InvalidDataException("回放中没有判定记录。");
    }

    private static string SanitizeFileName(string value)
    {
        const string portableInvalid = "<>:\"/\\|?*";
        StringBuilder builder = new(value.Length);
        bool insideTag = false;
        bool previousUnderscore = false;
        foreach (char character in value)
        {
            if (character == '<')
            {
                insideTag = true;
                continue;
            }
            if (insideTag)
            {
                if (character == '>')
                    insideTag = false;
                continue;
            }

            bool replace = char.IsControl(character)
                || char.IsWhiteSpace(character)
                || portableInvalid.Contains(character);
            char output = replace ? '_' : character;
            if (output == '_' && previousUnderscore)
                continue;
            builder.Append(output);
            previousUnderscore = output == '_';
        }

        string sanitized = builder.ToString().Trim(' ', '.', '_');
        if (string.IsNullOrEmpty(sanitized))
            sanitized = "Replay";
        return sanitized.Length <= 48 ? sanitized : sanitized[..48];
    }

    private static void WriteAtomic(string path, string json, string? temporaryPath = null)
    {
        temporaryPath ??= path + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }
}
