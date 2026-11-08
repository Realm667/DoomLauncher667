namespace DoomLauncher.WinUI.Services;

// Wire-format tree from Timothy Landers' Skulltag launcher codec (2009), MIT.
// Source: DoomseekerTeam/Doomseeker src/plugins/zandronum/huffman/huffman.cpp.
// See Licenses/Skulltag-Huffman-MIT.txt. Traversal and bounds handling are native C#.
internal static class ZandronumHuffman
{
    private static readonly byte[] TreeData = [
        0,0,0,1,128,0,0,0,3,38,34,2,1,80,3,110,144,67,0,2,1,74,3,243,142,37,2,3,124,58,182,0,
        0,1,36,0,3,221,131,3,245,163,1,35,3,113,85,0,1,41,1,77,3,199,130,0,1,206,3,185,153,3,70,118,
        0,3,3,5,0,0,1,24,0,2,3,198,190,63,2,3,139,186,75,0,1,44,2,3,240,218,56,3,40,39,0,0,
        2,2,3,244,247,81,65,0,3,9,125,3,68,60,0,0,1,25,3,191,138,3,86,17,0,1,23,3,220,178,2,3,
        165,194,14,1,0,2,2,0,0,2,1,208,3,150,157,181,1,222,2,3,216,230,211,0,2,2,3,252,141,10,42,0,
        2,3,134,135,104,1,103,3,187,225,95,32,0,0,0,0,0,0,1,57,1,61,3,183,237,0,0,3,233,234,3,246,
        203,2,3,250,147,79,1,129,0,1,7,3,143,136,1,20,3,179,148,0,0,0,3,28,106,3,101,87,1,66,0,3,
        180,219,3,227,241,0,1,26,1,251,3,229,214,3,54,69,0,0,0,0,0,3,231,212,3,156,176,3,93,83,0,3,
        96,253,3,30,13,0,0,2,3,175,254,94,3,159,27,2,1,8,3,204,226,78,0,0,0,3,107,88,1,31,3,137,
        169,2,2,3,215,145,6,4,1,127,0,1,99,3,209,217,0,3,213,238,3,177,170,1,132,0,0,0,2,3,22,12,
        114,2,2,3,158,197,97,45,0,1,46,1,112,3,174,249,0,3,224,102,2,3,171,151,193,0,0,0,3,15,16,3,
        2,168,1,49,3,91,146,0,1,48,3,173,29,0,3,19,126,3,92,242,0,0,0,0,0,0,3,205,192,2,3,235,
        149,255,2,3,223,184,248,0,0,3,108,236,3,111,90,2,3,117,115,71,0,0,3,11,50,0,3,188,119,1,122,3,
        167,162,1,160,1,133,3,123,21,0,0,2,1,59,2,3,155,154,98,43,0,3,76,51,2,3,201,116,72,2,0,2,
        3,109,100,121,2,3,195,232,18,1,0,2,0,1,164,2,3,120,189,73,0,1,196,3,239,210,3,64,62,89,0,0,
        1,33,2,3,228,161,55,2,3,84,152,47,0,0,2,3,207,172,140,3,82,166,0,3,53,105,1,52,3,202,200];
    private sealed record Node(int Value, Node? Left = null, Node? Right = null);
    private static readonly Node Root = Build();
    private static Node Build()
    {
        var position = 0;
        Node Branch()
        {
            var flags = TreeData[position++];
            Node Child(int bit) => (flags & (1 << bit)) != 0 ? new(TreeData[position++]) : Branch();
            return new(-1, Child(0), Child(1));
        }
        var result = Branch(); if (position != TreeData.Length) throw new InvalidDataException("Invalid protocol tree."); return result;
    }
    // The legacy protocol explicitly supports an uncompressed FF-prefixed request.
    public static byte[] Encode(ReadOnlySpan<byte> bytes)
    { var result = new byte[bytes.Length + 1]; result[0] = 255; bytes.CopyTo(result.AsSpan(1)); return result; }
    public static byte[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 65536) throw new InvalidDataException("Invalid query packet size.");
        if (bytes[0] == 255) return bytes[1..].ToArray();
        if (bytes[0] > 7) throw new InvalidDataException("Invalid Huffman padding.");
        var bits = (bytes.Length - 1) * 8 - bytes[0]; if (bits < 0) throw new InvalidDataException("Invalid Huffman length.");
        var result = new List<byte>(); var node = Root;
        for (var bit = 0; bit < bits; bit++)
        {
            node = (bytes[1 + bit / 8] & (1 << (bit % 8))) == 0 ? node.Left! : node.Right!;
            if (node.Value >= 0) { if (result.Count == 65536) throw new InvalidDataException("Query decompression limit exceeded."); result.Add((byte)node.Value); node = Root; }
        }
        if (!ReferenceEquals(node, Root)) throw new InvalidDataException("Incomplete Huffman symbol."); return result.ToArray();
    }
}
