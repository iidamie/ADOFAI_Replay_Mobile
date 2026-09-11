using System.IO.Compression;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Replay.Mobile;

/// <summary>
/// Small PNG/OpenGL ES fallback used when Unity cannot expose a native
/// Texture2D pointer to ImGui. It intentionally has no third-party image
/// dependency so Replay remains a standalone mod.
/// </summary>
internal static class ReplayOpenGlTexture
{
    private static readonly byte[] Signature =
        { 137, 80, 78, 71, 13, 10, 26, 10 };

    internal static bool TryLoadImage(
        string path,
        out nint textureId,
        out int width,
        out int height)
    {
        textureId = 0;
        width = 0;
        height = 0;
        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(path);
            if (image.Width > 1024 || image.Height > 1024)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(1024, 1024),
                }));
            }

            int rowBytes = checked(image.Width * 4);
            byte[] topDown = new byte[checked(rowBytes * image.Height)];
            image.CopyPixelDataTo(topDown);
            byte[] bottomUp = new byte[topDown.Length];
            for (int y = 0; y < image.Height; y++)
            {
                Buffer.BlockCopy(
                    topDown,
                    y * rowBytes,
                    bottomUp,
                    (image.Height - 1 - y) * rowBytes,
                    rowBytes);
            }
            return TryUploadRgba(
                bottomUp,
                image.Width,
                image.Height,
                out textureId,
                out width,
                out height);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryLoadPng(
        string path,
        out nint textureId,
        out int width,
        out int height)
    {
        try
        {
            return TryLoadPng(File.ReadAllBytes(path), out textureId, out width, out height);
        }
        catch
        {
            textureId = 0;
            width = 0;
            height = 0;
            return false;
        }
    }

    internal static bool TryLoadPng(
        byte[] data,
        out nint textureId,
        out int width,
        out int height)
    {
        textureId = 0;
        width = 0;
        height = 0;
        try
        {
            DecodedPng image = Decode(data);
            if (image.Width > 1024 || image.Height > 1024)
                image = Resize(image, 1024);
            return TryUploadRgba(
                image.Pixels,
                image.Width,
                image.Height,
                out textureId,
                out width,
                out height);
        }
        catch
        {
            return false;
        }
    }

    internal static void Delete(nint textureId)
    {
        if (textureId == 0)
            return;
        uint texture = unchecked((uint)textureId.ToInt64());
        try { Gl.DeleteTextures(1, ref texture); }
        catch { }
    }

    private static bool TryUploadRgba(
        byte[] pixels,
        int width,
        int height,
        out nint textureId,
        out int uploadedWidth,
        out int uploadedHeight)
    {
        textureId = 0;
        uploadedWidth = 0;
        uploadedHeight = 0;
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            return false;

        try
        {
            uint texture = 0;
            uint previousActiveTexture = Gl.Texture0;
            int previousTexture = 0;
            int previousUnpackAlignment = 4;
            int previousUnpackRowLength = 0;
            bool stateCaptured = false;
            bool adopted = false;
            try
            {
                Gl.ClearErrors();
                Gl.GetIntegerv(Gl.ActiveTextureState, out int activeTexture);
                Gl.GetIntegerv(Gl.UnpackAlignment, out previousUnpackAlignment);
                Gl.GetIntegerv(Gl.UnpackRowLength, out previousUnpackRowLength);
                if (activeTexture > 0)
                    previousActiveTexture = (uint)activeTexture;
                Gl.ActiveTexture(Gl.Texture0);
                Gl.GetIntegerv(Gl.TextureBinding2D, out previousTexture);
                stateCaptured = true;

                Gl.GenTextures(1, out texture);
                if (texture == 0)
                    return false;
                Gl.BindTexture(Gl.Texture2D, texture);
                Gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, (int)Gl.Linear);
                Gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, (int)Gl.Linear);
                Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, (int)Gl.ClampToEdge);
                Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, (int)Gl.ClampToEdge);
                Gl.PixelStorei(Gl.UnpackAlignment, 1);
                Gl.PixelStorei(Gl.UnpackRowLength, 0);
                Gl.TexImage2D(
                    Gl.Texture2D,
                    0,
                    (int)Gl.Rgba,
                    width,
                    height,
                    0,
                    Gl.Rgba,
                    Gl.UnsignedByte,
                    pixels);
                if (Gl.GetError() != Gl.NoError)
                    return false;

                adopted = true;
                textureId = (nint)(long)texture;
                uploadedWidth = width;
                uploadedHeight = height;
                return true;
            }
            finally
            {
                if (stateCaptured)
                {
                    try
                    {
                        Gl.ActiveTexture(Gl.Texture0);
                        Gl.BindTexture(Gl.Texture2D, (uint)Math.Max(0, previousTexture));
                        Gl.PixelStorei(Gl.UnpackAlignment, NormalizeAlignment(previousUnpackAlignment));
                        Gl.PixelStorei(Gl.UnpackRowLength, Math.Max(0, previousUnpackRowLength));
                        Gl.ActiveTexture(previousActiveTexture);
                    }
                    catch
                    {
                    }
                }
                if (!adopted && texture != 0)
                {
                    try { Gl.DeleteTextures(1, ref texture); }
                    catch { }
                }
            }
        }
        catch
        {
            textureId = 0;
            uploadedWidth = 0;
            uploadedHeight = 0;
            return false;
        }
    }

    private static DecodedPng Decode(byte[] data)
    {
        if (data.Length < Signature.Length
            || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("PNG signature is invalid");

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using MemoryStream compressed = new();
        int offset = Signature.Length;
        bool foundHeader = false;
        bool foundEnd = false;

        while (offset + 12 <= data.Length)
        {
            uint chunkLength = ReadUInt32(data, offset);
            offset += 4;
            if (chunkLength > int.MaxValue || offset + 4L + chunkLength + 4L > data.Length)
                throw new InvalidDataException("PNG chunk is truncated");
            int length = (int)chunkLength;
            int typeOffset = offset;
            int payloadOffset = offset + 4;
            offset = payloadOffset + length + 4;
            byte type0 = data[typeOffset];
            byte type1 = data[typeOffset + 1];
            byte type2 = data[typeOffset + 2];
            byte type3 = data[typeOffset + 3];

            if (type0 == 'I' && type1 == 'H' && type2 == 'D' && type3 == 'R')
            {
                if (length != 13)
                    throw new InvalidDataException("PNG IHDR is invalid");
                width = checked((int)ReadUInt32(data, payloadOffset));
                height = checked((int)ReadUInt32(data, payloadOffset + 4));
                bitDepth = data[payloadOffset + 8];
                colorType = data[payloadOffset + 9];
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                    throw new InvalidDataException("PNG dimensions are invalid");
                if (data[payloadOffset + 10] != 0
                    || data[payloadOffset + 11] != 0
                    || data[payloadOffset + 12] != 0)
                    throw new InvalidDataException("PNG interlace is unsupported");
                foundHeader = true;
            }
            else if (type0 == 'P' && type1 == 'L' && type2 == 'T' && type3 == 'E')
            {
                palette = data.AsSpan(payloadOffset, length).ToArray();
            }
            else if (type0 == 't' && type1 == 'R' && type2 == 'N' && type3 == 'S')
            {
                transparency = data.AsSpan(payloadOffset, length).ToArray();
            }
            else if (type0 == 'I' && type1 == 'D' && type2 == 'A' && type3 == 'T')
            {
                compressed.Write(data, payloadOffset, length);
            }
            else if (type0 == 'I' && type1 == 'E' && type2 == 'N' && type3 == 'D')
            {
                foundEnd = true;
                break;
            }
        }

        if (!foundHeader || !foundEnd || bitDepth != 8)
            throw new InvalidDataException("PNG header or bit depth is unsupported");

        int bytesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("PNG color type is unsupported"),
        };
        if (colorType == 3 && (palette == null || palette.Length < 3 || palette.Length % 3 != 0))
            throw new InvalidDataException("PNG palette is invalid");

        int rowBytes = checked(width * bytesPerPixel);
        int expectedSize = checked((rowBytes + 1) * height);
        using MemoryStream input = new(compressed.ToArray(), writable: false);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new(expectedSize);
        zlib.CopyTo(output);
        byte[] filtered = output.ToArray();
        if (filtered.Length < expectedSize)
            throw new InvalidDataException("PNG pixels are truncated");

        byte[] pixels = new byte[checked(width * height * 4)];
        byte[] previous = new byte[rowBytes];
        byte[] current = new byte[rowBytes];
        int sourceOffset = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[sourceOffset++];
            Array.Copy(filtered, sourceOffset, current, 0, rowBytes);
            sourceOffset += rowBytes;
            Unfilter(current, previous, bytesPerPixel, filter);
            int outputRow = height - 1 - y;
            for (int x = 0; x < width; x++)
                WriteRgba(
                    pixels,
                    (outputRow * width + x) * 4,
                    current,
                    x * bytesPerPixel,
                    colorType,
                    palette,
                    transparency);
            (current, previous) = (previous, current);
        }
        return new DecodedPng { Width = width, Height = height, Pixels = pixels };
    }

    private static DecodedPng Resize(DecodedPng source, int maxDimension)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension)
            return source;
        float scale = Math.Min(
            maxDimension / (float)source.Width,
            maxDimension / (float)source.Height);
        int width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(source.Height * scale));
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Clamp((int)((y + 0.5f) * source.Height / height), 0, source.Height - 1);
            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Clamp((int)((x + 0.5f) * source.Width / width), 0, source.Width - 1);
                Buffer.BlockCopy(
                    source.Pixels,
                    (sourceY * source.Width + sourceX) * 4,
                    pixels,
                    (y * width + x) * 4,
                    4);
            }
        }
        return new DecodedPng { Width = width, Height = height, Pixels = pixels };
    }

    private static void Unfilter(byte[] row, byte[] previous, int bytesPerPixel, int filter)
    {
        if (filter == 0)
            return;
        if (filter < 0 || filter > 4)
            throw new InvalidDataException("PNG filter is invalid");
        for (int index = 0; index < row.Length; index++)
        {
            int left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
            int above = previous[index];
            int upperLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
            int predictor = filter switch
            {
                1 => left,
                2 => above,
                3 => (left + above) / 2,
                4 => Paeth(left, above, upperLeft),
                _ => 0,
            };
            row[index] = (byte)(row[index] + predictor);
        }
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
            return left;
        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static void WriteRgba(
        byte[] destination,
        int destinationOffset,
        byte[] source,
        int sourceOffset,
        int colorType,
        byte[]? palette,
        byte[]? transparency)
    {
        byte red;
        byte green;
        byte blue;
        byte alpha;
        switch (colorType)
        {
            case 0:
                red = green = blue = source[sourceOffset];
                alpha = transparency is { Length: >= 2 }
                    && source[sourceOffset] == transparency[1]
                    && transparency[0] == 0 ? (byte)0 : (byte)255;
                break;
            case 2:
                red = source[sourceOffset];
                green = source[sourceOffset + 1];
                blue = source[sourceOffset + 2];
                alpha = 255;
                break;
            case 3:
                int paletteOffset = source[sourceOffset] * 3;
                if (palette == null || paletteOffset + 2 >= palette.Length)
                    throw new InvalidDataException("PNG palette index is invalid");
                red = palette[paletteOffset];
                green = palette[paletteOffset + 1];
                blue = palette[paletteOffset + 2];
                alpha = transparency != null && source[sourceOffset] < transparency.Length
                    ? transparency[source[sourceOffset]]
                    : (byte)255;
                break;
            case 4:
                red = green = blue = source[sourceOffset];
                alpha = source[sourceOffset + 1];
                break;
            case 6:
                red = source[sourceOffset];
                green = source[sourceOffset + 1];
                blue = source[sourceOffset + 2];
                alpha = source[sourceOffset + 3];
                break;
            default:
                throw new InvalidDataException("PNG color type is unsupported");
        }
        destination[destinationOffset] = red;
        destination[destinationOffset + 1] = green;
        destination[destinationOffset + 2] = blue;
        destination[destinationOffset + 3] = alpha;
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => ((uint)data[offset] << 24)
            | ((uint)data[offset + 1] << 16)
            | ((uint)data[offset + 2] << 8)
            | data[offset + 3];

    private static int NormalizeAlignment(int value)
        => value is 1 or 2 or 4 or 8 ? value : 4;

    private sealed class DecodedPng
    {
        internal int Width;
        internal int Height;
        internal byte[] Pixels = Array.Empty<byte>();
    }

    private static class Gl
    {
        internal const uint NoError = 0;
        internal const uint Texture2D = 0x0DE1;
        internal const uint Texture0 = 0x84C0;
        internal const uint ActiveTextureState = 0x84E0;
        internal const uint TextureBinding2D = 0x8069;
        internal const uint TextureMinFilter = 0x2801;
        internal const uint TextureMagFilter = 0x2800;
        internal const uint TextureWrapS = 0x2802;
        internal const uint TextureWrapT = 0x2803;
        internal const uint UnpackAlignment = 0x0CF5;
        internal const uint UnpackRowLength = 0x0CF2;
        internal const uint Linear = 0x2601;
        internal const uint ClampToEdge = 0x812F;
        internal const uint Rgba = 0x1908;
        internal const uint UnsignedByte = 0x1401;

        [DllImport("libGLESv3.so", EntryPoint = "glActiveTexture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ActiveTexture(uint texture);

        [DllImport("libGLESv3.so", EntryPoint = "glBindTexture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void BindTexture(uint target, uint texture);

        [DllImport("libGLESv3.so", EntryPoint = "glDeleteTextures", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void DeleteTextures(int count, ref uint textures);

        [DllImport("libGLESv3.so", EntryPoint = "glGenTextures", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void GenTextures(int count, out uint textures);

        [DllImport("libGLESv3.so", EntryPoint = "glGetError", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint GetError();

        [DllImport("libGLESv3.so", EntryPoint = "glGetIntegerv", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void GetIntegerv(uint name, out int value);

        [DllImport("libGLESv3.so", EntryPoint = "glPixelStorei", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void PixelStorei(uint name, int value);

        [DllImport("libGLESv3.so", EntryPoint = "glTexImage2D", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TexImage2D(
            uint target,
            int level,
            int internalFormat,
            int width,
            int height,
            int border,
            uint format,
            uint type,
            byte[] pixels);

        [DllImport("libGLESv3.so", EntryPoint = "glTexParameteri", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TexParameteri(uint target, uint name, int value);

        internal static void ClearErrors()
        {
            for (int index = 0; index < 8 && GetError() != NoError; index++) { }
        }
    }
}
