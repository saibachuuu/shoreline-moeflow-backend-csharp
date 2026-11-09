using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Shoreline.Api.Security;

// Raster output contains no text, metadata or recoverable answer string.
public static class CaptchaImage
{
    private static readonly string[] Digits =
    [
        "01110100011001110101110011000101110", "00100011000010000100001000010001110",
        "01110100010000100010001000100011111", "11110000010000101110000010000111110",
        "00010001100101010010111110001000010", "11111100001000011110000010000111110",
        "01110100001000011110100011000101110", "11111000010001000100010000100001000",
        "01110100011000101110100011000101110", "01110100011000101111000010000101110"
    ];

    public static byte[] Render(string answer)
    {
        const int width = 140, height = 48;
        var pixels = Enumerable.Repeat((byte)250, height * (1 + width * 3)).ToArray();
        for (var y = 0; y < height; y++) pixels[y * (1 + width * 3)] = 0;
        void Dot(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var i = y * (1 + width * 3) + 1 + x * 3;
            pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b;
        }
        for (var digit = 0; digit < answer.Length; digit++)
        {
            var pattern = Digits[answer[digit] - '0'];
            var top = RandomNumberGenerator.GetInt32(6, 14);
            var tilt = RandomNumberGenerator.GetInt32(-2, 3);
            for (var y = 0; y < 28; y++)
            for (var x = 0; x < 20; x++)
                if (pattern[y / 4 * 5 + x / 4] == '1')
                    Dot(12 + digit * 31 + x + tilt * y / 10, top + y, 30, 95, 165);
        }
        for (var i = 0; i < 130; i++) Dot(RandomNumberGenerator.GetInt32(width), RandomNumberGenerator.GetInt32(height), 90, 110, 120);
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(pixels);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] content)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, content.Length);
        output.Write(size);
        byte[] body = [.. Encoding.ASCII.GetBytes(kind), .. content];
        output.Write(body);
        uint crc = uint.MaxValue;
        foreach (var value in body)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(size, ~crc);
        output.Write(size);
    }
}
