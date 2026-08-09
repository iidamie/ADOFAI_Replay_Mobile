using System.IO.Compression;
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
        string fileName = $"{timestamp}_{song}_{replay.SessionId[..Math.Min(8, replay.SessionId.Length)]}.rpl2";
        string path = Path.Combine(directory, fileName);
        string temporaryPath = path + ".tmp";

        Rpl2ReplayCodec.Write(path, replay, temporaryPath);
        return path;
    }

    internal ReplayData Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (Rpl2ReplayCodec.HasMagic(stream))
        {
            stream.Position = 0;
            ReplayData rpl2Replay = Rpl2ReplayCodec.Read(stream);
            Validate(rpl2Replay);
            return rpl2Replay;
        }

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

    internal string UpdateTitle(string path, string title)
    {
        ReplayData replay = Load(path);
        replay.Title = NormalizeTitle(title);
        Validate(replay);
        if (Rpl2ReplayCodec.IsFile(path))
        {
            Rpl2ReplayCodec.Write(path, replay);
        }
        else
        {
            string json = JsonSerializer.Serialize(replay, ReplayJsonContext.Default.ReplayData);
            WriteAtomic(path, json);
        }
        return replay.Title;
    }

    internal List<ReplayFileEntry> Scan(string configuredPath)
    {
        string directory = ResolveDirectory(configuredPath);
        if (!Directory.Exists(directory))
            return new List<ReplayFileEntry>();

        List<ReplayFileEntry> entries = new();
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".rpl", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".rpl2", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                bool nativeFormat = IsNativeFormat(path);
                ReplayData replay = Load(path);
                entries.Add(new ReplayFileEntry(
                    path,
                    replay.Title,
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
                    "",
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
        if (Rpl2ReplayCodec.HasMagic(stream))
            return true;
        return ReadFirstContentByte(stream) == '{';
    }

    private static void Validate(ReplayData replay)
    {
        if (replay.FormatVersion is not (1 or 2))
            throw new InvalidDataException($"不支持回放格式版本 {replay.FormatVersion}。");
        replay.Hits ??= new List<ReplayHit>();
        replay.TouchEvents ??= new List<ReplayTouchInput>();
        replay.SongName = string.IsNullOrWhiteSpace(replay.SongName) ? "Unknown" : replay.SongName.Trim();
        replay.Title = NormalizeTitle(replay.Title);
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

        for (int index = replay.TouchEvents.Count - 1; index >= 0; index--)
        {
            ReplayTouchInput input = replay.TouchEvents[index];
            if (input == null)
            {
                replay.TouchEvents.RemoveAt(index);
                continue;
            }
            input.TimeMilliseconds = Math.Clamp(input.TimeMilliseconds, 0L, 86_400_000L);
            input.X = float.IsFinite(input.X) ? input.X : 0f;
            input.Y = float.IsFinite(input.Y) ? input.Y : 0f;
            input.SourceWidth = float.IsFinite(input.SourceWidth) && input.SourceWidth > 0f
                ? input.SourceWidth
                : 0f;
            input.SourceHeight = float.IsFinite(input.SourceHeight) && input.SourceHeight > 0f
                ? input.SourceHeight
                : 0f;
        }
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

    private static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        StringBuilder builder = new(Math.Min(value.Length, 96));
        bool pendingSpace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsControl(character))
                continue;
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace && builder.Length < 96)
                builder.Append(' ');
            pendingSpace = false;
            if (builder.Length >= 96)
                break;
            builder.Append(character);
        }
        return builder.ToString().TrimEnd();
    }

    private static void WriteAtomic(string path, string json, string? temporaryPath = null)
    {
        temporaryPath ??= path + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }
}

/// <summary>
/// Compact binary replay container. The header is intentionally independent of
/// ReplayData.FormatVersion so the loader can reject unsupported containers before
/// attempting decompression.
/// </summary>
internal static class Rpl2ReplayCodec
{
    private static readonly byte[] Magic = { (byte)'R', (byte)'P', (byte)'L', (byte)'2' };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private const byte ContainerVersion = 1;
    private const byte DeflateCompression = 1;
    private const int PayloadLengthOffset = 8;
    private const long MaximumFileSize = 64L * 1024L * 1024L;
    private const int MaximumPayloadSize = 256 * 1024 * 1024;
    private const int MaximumCollectionCount = 2_000_000;
    private const int MaximumStringBytes = 1 * 1024 * 1024;

    internal static bool IsFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return HasMagic(stream);
    }

    internal static bool HasMagic(Stream stream)
    {
        if (!stream.CanSeek)
            return false;

        long position = stream.Position;
        try
        {
            for (int index = 0; index < Magic.Length; index++)
            {
                if (stream.ReadByte() != Magic[index])
                    return false;
            }
            return true;
        }
        finally
        {
            stream.Position = position;
        }
    }

    internal static void Write(string path, ReplayData replay, string? temporaryPath = null)
    {
        temporaryPath ??= path + ".tmp";
        long payloadLength;
        try
        {
            using (FileStream file = new(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                using (BinaryWriter header = new(file, Utf8, leaveOpen: true))
                {
                    header.Write(Magic);
                    header.Write(ContainerVersion);
                    header.Write(DeflateCompression);
                    header.Write((ushort)0);
                    header.Write(0);
                }

                long uncompressedLength;
                using (DeflateStream deflate = new(file, CompressionLevel.Optimal, leaveOpen: true))
                using (CountingWriteStream payloadStream = new(deflate, MaximumPayloadSize, leaveOpen: true))
                using (BinaryWriter payload = new(payloadStream, Utf8, leaveOpen: true))
                {
                    WritePayload(payload, replay);
                    payload.Flush();
                    uncompressedLength = payloadStream.BytesWritten;
                }

                if (uncompressedLength > int.MaxValue)
                    throw new InvalidDataException("回放未压缩数据超过 2 GB 限制。");

                file.Position = PayloadLengthOffset;
                using (BinaryWriter length = new(file, Utf8, leaveOpen: true))
                {
                    length.Write((int)uncompressedLength);
                    length.Flush();
                }
                file.Flush(true);
                payloadLength = file.Length;
            }

            if (payloadLength > MaximumFileSize)
                throw new InvalidDataException("回放压缩文件超过 64 MB 限制。");

            File.Move(temporaryPath, path, true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original save error if cleanup is unavailable.
            }
            throw;
        }

    }

    internal static ReplayData Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > MaximumFileSize)
            throw new InvalidDataException("RPL2 回放文件超过 64 MB 限制。");

        using BinaryReader header = new(stream, Utf8, leaveOpen: true);
        for (int index = 0; index < Magic.Length; index++)
        {
            if (header.ReadByte() != Magic[index])
                throw new InvalidDataException("不是有效的 RPL2 回放文件。");
        }

        if (header.ReadByte() != ContainerVersion)
            throw new InvalidDataException("不支持的 RPL2 容器版本。");
        if (header.ReadByte() != DeflateCompression)
            throw new InvalidDataException("不支持的 RPL2 压缩方式。");
        if (header.ReadUInt16() != 0)
            throw new InvalidDataException("RPL2 头部标志无效。");

        int payloadLength = header.ReadInt32();
        if (payloadLength <= 0 || payloadLength > MaximumPayloadSize)
            throw new InvalidDataException("RPL2 未压缩长度无效。");

        using DeflateStream deflate = new(stream, CompressionMode.Decompress, leaveOpen: true);
        using CountingReadStream payloadStream = new(deflate, payloadLength);
        using BinaryReader payload = new(payloadStream, Utf8, leaveOpen: true);
        ReplayData replay = ReadPayload(payload);
        Drain(payloadStream);

        if (payloadStream.BytesRead != payloadLength)
            throw new InvalidDataException("RPL2 数据长度与头部不一致。");
        return replay;
    }

    private static void WritePayload(BinaryWriter writer, ReplayData replay)
    {
        EnsureCollectionCount(replay.Hits.Count);
        EnsureCollectionCount(replay.TouchEvents.Count);

        WriteVarUInt(writer, checked((uint)replay.FormatVersion));
        WriteString(writer, replay.ModVersion ?? "");
        WriteString(writer, replay.SessionId ?? "");
        writer.Write(replay.RecordedAtUtc.ToUniversalTime().Ticks);
        WriteString(writer, replay.SongName ?? "");
        WriteString(writer, replay.Title ?? "");
        WriteString(writer, replay.ArtistName ?? "");
        WriteString(writer, replay.LevelPath ?? "");
        WriteString(writer, replay.SceneName ?? "");
        WriteString(writer, replay.LevelId ?? "");

        byte replayFlags = 0;
        if (replay.IsOfficialLevel)
            replayFlags |= 1;
        if (replay.Completed)
            replayFlags |= 2;
        writer.Write(replayFlags);
        writer.Write(replay.Speed);
        writer.Write(replay.Bpm);
        WriteVarInt(writer, replay.StartTile);
        WriteVarInt(writer, replay.EndTile);
        WriteVarInt(writer, replay.TotalTiles);

        WriteVarUInt(writer, checked((uint)replay.Hits.Count));
        int previousSequence = 0;
        foreach (ReplayHit hit in replay.Hits)
        {
            WriteVarLong(writer, (long)hit.SequenceId - previousSequence);
            writer.Write(hit.HitAngleOffset);
            WriteVarInt(writer, hit.HitMargin);
            byte hitFlags = 0;
            if (hit.NoFailHit)
                hitFlags |= 1;
            if (hit.AutoHit)
                hitFlags |= 2;
            writer.Write(hitFlags);
            previousSequence = hit.SequenceId;
        }

        WriteVarUInt(writer, checked((uint)replay.TouchEvents.Count));
        long previousTime = 0;
        uint previousX = 0;
        uint previousY = 0;
        uint previousWidth = 0;
        uint previousHeight = 0;
        foreach (ReplayTouchInput input in replay.TouchEvents)
        {
            WriteVarLong(writer, input.TimeMilliseconds - previousTime);
            WriteVarInt(writer, input.Action);
            WriteVarInt(writer, input.PointerId);

            uint x = unchecked((uint)BitConverter.SingleToInt32Bits(input.X));
            uint y = unchecked((uint)BitConverter.SingleToInt32Bits(input.Y));
            uint width = unchecked((uint)BitConverter.SingleToInt32Bits(input.SourceWidth));
            uint height = unchecked((uint)BitConverter.SingleToInt32Bits(input.SourceHeight));
            byte touchFlags = 0;
            if (width != previousWidth)
                touchFlags |= 1;
            if (height != previousHeight)
                touchFlags |= 2;
            writer.Write(touchFlags);
            WriteVarUInt(writer, x ^ previousX);
            WriteVarUInt(writer, y ^ previousY);
            if ((touchFlags & 1) != 0)
                WriteVarUInt(writer, width ^ previousWidth);
            if ((touchFlags & 2) != 0)
                WriteVarUInt(writer, height ^ previousHeight);
            previousTime = input.TimeMilliseconds;
            previousX = x;
            previousY = y;
            previousWidth = width;
            previousHeight = height;
        }
    }

    private static ReplayData ReadPayload(BinaryReader reader)
    {
        uint formatVersion = ReadVarUInt(reader);
        if (formatVersion > int.MaxValue)
            throw new InvalidDataException("RPL2 回放版本无效。");

        string modVersion = ReadString(reader);
        string sessionId = ReadString(reader);
        long recordedAtTicks = reader.ReadInt64();
        if (recordedAtTicks < DateTime.MinValue.Ticks || recordedAtTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("RPL2 录制时间无效。");

        string songName = ReadString(reader);
        string title = ReadString(reader);
        string artistName = ReadString(reader);
        string levelPath = ReadString(reader);
        string sceneName = ReadString(reader);
        string levelId = ReadString(reader);
        byte replayFlags = reader.ReadByte();
        if ((replayFlags & ~3) != 0)
            throw new InvalidDataException("RPL2 回放标志无效。");

        ReplayData replay = new()
        {
            FormatVersion = (int)formatVersion,
            ModVersion = modVersion,
            SessionId = sessionId,
            RecordedAtUtc = new DateTime(recordedAtTicks, DateTimeKind.Utc),
            SongName = songName,
            Title = title,
            ArtistName = artistName,
            LevelPath = levelPath,
            SceneName = sceneName,
            LevelId = levelId,
            IsOfficialLevel = (replayFlags & 1) != 0,
            Completed = (replayFlags & 2) != 0,
            Speed = reader.ReadSingle(),
            Bpm = reader.ReadSingle(),
            StartTile = ReadInt32(reader, "起始瓦片"),
            EndTile = ReadInt32(reader, "结束瓦片"),
            TotalTiles = ReadInt32(reader, "总瓦片"),
        };

        int hitCount = ReadCollectionCount(reader, "判定");
        replay.Hits = new List<ReplayHit>(hitCount);
        long previousSequence = 0;
        for (int index = 0; index < hitCount; index++)
        {
            previousSequence = checked(previousSequence + ReadVarLong(reader));
            if (previousSequence < int.MinValue || previousSequence > int.MaxValue)
                throw new InvalidDataException("RPL2 判定序号超出范围。");
            double hitAngleOffset = reader.ReadDouble();
            int hitMargin = ReadInt32(reader, "判定等级");
            byte hitFlags = reader.ReadByte();
            if ((hitFlags & ~3) != 0)
                throw new InvalidDataException("RPL2 判定标志无效。");

            replay.Hits.Add(new ReplayHit
            {
                SequenceId = (int)previousSequence,
                HitAngleOffset = hitAngleOffset,
                HitMargin = hitMargin,
                NoFailHit = (hitFlags & 1) != 0,
                AutoHit = (hitFlags & 2) != 0,
            });
        }

        int touchCount = ReadCollectionCount(reader, "触摸");
        replay.TouchEvents = new List<ReplayTouchInput>(touchCount);
        long previousTime = 0;
        uint previousX = 0;
        uint previousY = 0;
        uint previousWidth = 0;
        uint previousHeight = 0;
        for (int index = 0; index < touchCount; index++)
        {
            previousTime = checked(previousTime + ReadVarLong(reader));
            byte touchFlags;
            int action = ReadInt32(reader, "触摸动作");
            int pointerId = ReadInt32(reader, "指针 ID");
            touchFlags = reader.ReadByte();
            if ((touchFlags & ~3) != 0)
                throw new InvalidDataException("RPL2 触摸标志无效。");

            previousX ^= ReadVarUInt(reader);
            previousY ^= ReadVarUInt(reader);
            if ((touchFlags & 1) != 0)
                previousWidth ^= ReadVarUInt(reader);
            if ((touchFlags & 2) != 0)
                previousHeight ^= ReadVarUInt(reader);

            replay.TouchEvents.Add(new ReplayTouchInput
            {
                TimeMilliseconds = previousTime,
                Action = action,
                PointerId = pointerId,
                X = BitConverter.Int32BitsToSingle(unchecked((int)previousX)),
                Y = BitConverter.Int32BitsToSingle(unchecked((int)previousY)),
                SourceWidth = BitConverter.Int32BitsToSingle(unchecked((int)previousWidth)),
                SourceHeight = BitConverter.Int32BitsToSingle(unchecked((int)previousHeight)),
            });
        }
        return replay;
    }

    private static int ReadInt32(BinaryReader reader, string field)
    {
        long value = ReadVarLong(reader);
        if (value < int.MinValue || value > int.MaxValue)
            throw new InvalidDataException($"RPL2 {field}超出范围。");
        return (int)value;
    }

    private static int ReadCollectionCount(BinaryReader reader, string field)
    {
        uint count = ReadVarUInt(reader);
        if (count > MaximumCollectionCount)
            throw new InvalidDataException($"RPL2 {field}数量过大。");
        return (int)count;
    }

    private static void EnsureCollectionCount(int count)
    {
        if (count < 0 || count > MaximumCollectionCount)
            throw new InvalidDataException("回放事件数量过大。");
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new InvalidDataException("回放元数据字符串过长。");
        WriteVarUInt(writer, checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        uint length = ReadVarUInt(reader);
        if (length > MaximumStringBytes)
            throw new InvalidDataException("RPL2 元数据字符串过长。");
        byte[] bytes = reader.ReadBytes((int)length);
        if (bytes.Length != length)
            throw new EndOfStreamException("RPL2 元数据字符串不完整。");
        return Utf8.GetString(bytes);
    }

    private static void WriteVarInt(BinaryWriter writer, int value)
        => WriteVarLong(writer, value);

    private static void WriteVarLong(BinaryWriter writer, long value)
    {
        ulong encoded = value < 0
            ? (ulong)(-(value + 1)) * 2UL + 1UL
            : (ulong)value * 2UL;
        WriteVarUInt(writer, encoded);
    }

    private static void WriteVarUInt(BinaryWriter writer, uint value)
        => WriteVarUInt(writer, (ulong)value);

    private static void WriteVarUInt(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }

    private static long ReadVarLong(BinaryReader reader)
    {
        ulong encoded = ReadVarUInt64(reader);
        long value = (long)(encoded >> 1);
        return (encoded & 1) == 0 ? value : ~value;
    }

    private static uint ReadVarUInt(BinaryReader reader)
    {
        ulong value = ReadVarUInt64(reader);
        if (value > uint.MaxValue)
            throw new InvalidDataException("RPL2 变长整数超出范围。");
        return (uint)value;
    }

    private static ulong ReadVarUInt64(BinaryReader reader)
    {
        ulong value = 0;
        for (int index = 0; index < 10; index++)
        {
            int raw = reader.ReadByte();
            if (raw < 0)
                throw new EndOfStreamException("RPL2 变长整数不完整。");
            if (index == 9 && (raw & 0xFE) != 0)
                throw new InvalidDataException("RPL2 变长整数溢出。");
            value |= (ulong)(raw & 0x7F) << (index * 7);
            if ((raw & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("RPL2 变长整数过长。");
    }

    private static void Drain(CountingReadStream stream)
    {
        byte[] buffer = new byte[8192];
        while (stream.Read(buffer, 0, buffer.Length) != 0)
        {
        }
    }

    private sealed class CountingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly bool _leaveOpen;

        internal CountingWriteStream(Stream inner, long limit, bool leaveOpen)
        {
            _inner = inner;
            _limit = limit;
            _leaveOpen = leaveOpen;
        }

        internal long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _inner.WriteByte(value);
            BytesWritten++;
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || BytesWritten > _limit - count)
                throw new InvalidDataException("回放未压缩数据超过 256 MB 限制。");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;

        internal CountingReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return 0;
            long remaining = _limit - BytesRead;
            if (remaining <= 0)
            {
                ThrowIfTrailingData();
                return 0;
            }

            int read = _inner.Read(buffer, offset, (int)Math.Min(remaining, count));
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;
            long remaining = _limit - BytesRead;
            if (remaining <= 0)
            {
                ThrowIfTrailingData();
                return 0;
            }

            int read = _inner.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
            BytesRead += read;
            return read;
        }

        public override int ReadByte()
        {
            if (BytesRead >= _limit)
            {
                ThrowIfTrailingData();
                return -1;
            }
            int value = _inner.ReadByte();
            if (value >= 0)
                BytesRead++;
            return value;
        }

        private void ThrowIfTrailingData()
        {
            if (_inner.ReadByte() >= 0)
                throw new InvalidDataException("RPL2 未压缩数据超过头部声明长度。");
        }

        protected override void Dispose(bool disposing)
        {
            // The DeflateStream owns the underlying compressed stream.
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
