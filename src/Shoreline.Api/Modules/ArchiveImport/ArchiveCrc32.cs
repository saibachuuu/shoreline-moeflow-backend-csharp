namespace Shoreline.Api.Modules.ArchiveImport;

/// <summary>Standard IEEE CRC-32 used by ZIP entries. Lets the worker verify data it actually read.</summary>
public static class ArchiveCrc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0xffffffffu, data) ^ 0xffffffffu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
