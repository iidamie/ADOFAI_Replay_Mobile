using System.Globalization;
using System.Text.Json;

namespace Replay.Mobile;

internal sealed class ReplayChartAudioPreview
{
    internal string SongPath { get; init; } = "";
    internal int PreviewSongStart { get; init; }
    internal int PreviewSongDuration { get; init; }
    internal int Volume { get; init; } = 100;
}

internal sealed class ReplayChartPreviewSource
{
    internal string Path { get; }

    internal ReplayChartPreviewSource(string path)
    {
        Path = path;
    }
}

/// <summary>
/// Reads the chart settings used by the game's preview. Replay files
/// store the loose chart path, so this work only happens when a manager entry
/// is selected and never in the game update loop.
/// </summary>
internal static class ReplayChartMetadata
{
    private static readonly JsonDocumentOptions ChartJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    internal static ReplayChartPreviewSource? PreparePreviewImage(string sourcePath)
    {
        string? chartPath = ResolveChartFile(sourcePath);
        if (chartPath == null)
            return null;

        try
        {
            string? relativePath = ReadSettingString(chartPath, "previewImage");
            string? imagePath = ResolveChartAsset(chartPath, relativePath);
            return imagePath == null ? null : new ReplayChartPreviewSource(imagePath);
        }
        catch
        {
            return null;
        }
    }

    internal static ReplayChartAudioPreview? ReadAudioPreview(string sourcePath)
    {
        string? chartPath = ResolveChartFile(sourcePath);
        if (chartPath == null)
            return null;

        try
        {
            using JsonDocument document = ReadChartJson(chartPath);
            if (!document.RootElement.TryGetProperty("settings", out JsonElement settings)
                || settings.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!settings.TryGetProperty("songFilename", out JsonElement songValue)
                || songValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? songPath = ResolveChartAsset(chartPath, songValue.GetString());
            if (songPath == null)
                return null;

            return new ReplayChartAudioPreview
            {
                SongPath = songPath,
                PreviewSongStart = Math.Max(0, ReadInt(settings, "previewSongStart")),
                PreviewSongDuration = Math.Max(0, ReadInt(settings, "previewSongDuration")),
                Volume = NormalizeVolume(ReadInt(settings, "volume")),
            };
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveChartFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        try
        {
            string path = Path.GetFullPath(sourcePath.Trim());
            if (File.Exists(path))
            {
                // LevelPath points at the exact chart used by the replay;
                // keep explicit sub/tutorial charts valid as well.
                return Path.GetExtension(path).Equals(".adofai", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : null;
            }

            if (!Directory.Exists(path))
                return null;

            string[] files = Directory.EnumerateFiles(path, "*.adofai", SearchOption.AllDirectories)
                .Where(candidate => IsChartFile(candidate))
                .ToArray();
            if (files.Length == 0)
                return null;

            return files.FirstOrDefault(candidate =>
                       string.Equals(
                           Path.GetFileName(candidate),
                           "main.adofai",
                           StringComparison.OrdinalIgnoreCase))
                ?? (files.Length == 1 ? files[0] : null);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSettingString(string chartPath, string name)
    {
        using JsonDocument document = ReadChartJson(chartPath);
        if (!document.RootElement.TryGetProperty("settings", out JsonElement settings)
            || settings.ValueKind != JsonValueKind.Object
            || !settings.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    private static JsonDocument ReadChartJson(string chartPath)
    {
        using FileStream stream = File.OpenRead(chartPath);
        return JsonDocument.Parse(stream, ChartJsonOptions);
    }

    private static string? ResolveChartAsset(string chartPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        string normalized = relativePath.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Contains('\0'))
        {
            return null;
        }

        foreach (string part in normalized.Split('/'))
        {
            if (part is ".." or "")
                return null;
        }

        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath)) ?? "";
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return null;
        string assetPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = baseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(assetPath))
        {
            return null;
        }
        return assetPath;
    }

    private static bool IsChartFile(string path)
    {
        if (!Path.GetExtension(path).Equals(".adofai", StringComparison.OrdinalIgnoreCase))
            return false;
        string stem = Path.GetFileNameWithoutExtension(path);
        return !HasOnlyOptionalDigitsAfter(stem, "backup")
            && !HasOnlyOptionalDigitsAfter(stem, "sub");
    }

    private static bool HasOnlyOptionalDigitsAfter(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        for (int index = prefix.Length; index < value.Length; index++)
        {
            if (value[index] < '0' || value[index] > '9')
                return false;
        }
        return true;
    }

    private static int ReadInt(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out JsonElement value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        return int.TryParse(
            value.ToString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : 0;
    }

    private static int NormalizeVolume(int value)
        => value <= 0 ? 100 : Math.Clamp(value, 0, 100);
}
