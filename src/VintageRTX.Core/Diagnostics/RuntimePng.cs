using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VintageRTX.Core.Diagnostics;

/// <summary>RGBA8 screenshot writer: no gamma conversion, resampling, metadata credentials or external dependencies.</summary>
public static class RuntimePng
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> rgba, bool bottomUp = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (width <= 0 || height <= 0 || (long)width * height > 16_777_216 || rgba.Length != (long)width * height * 4)
            throw new ArgumentOutOfRangeException(nameof(width));
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13]; header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; header[9] = 6; Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // PNG filter None, retaining exact display bytes.
                int row = bottomUp ? height - 1 - y : y;
                z.Write(rgba.Slice(row * width * 4, width * 4));
            }
        }
        Chunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        Chunk(output, "IEND", []);
    }
    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> four = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(four, payload.Length); output.Write(four);
        byte[] name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(payload);
        uint crc = 0xffffffff;
        foreach (byte b in name) crc = Update(crc, b);
        foreach (byte b in payload) crc = Update(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(four, ~crc); output.Write(four);
    }
    private static uint Update(uint crc, byte value) => (crc >> 8) ^ CrcTable[(byte)(crc ^ value)];
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            table[i] = crc;
        }
        return table;
    }
}
