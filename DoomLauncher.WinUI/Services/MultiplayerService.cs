using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DoomLauncher.Modern.Core.Launch;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

public sealed record ServerModFile(string Name, string? Md5);
public sealed record MultiplayerServer(IPEndPoint Endpoint, string Name, string Map, string GameMode, string Iwad,
    string Version, int Players, int MaximumPlayers, bool PasswordRequired, long Ping, IReadOnlyList<ServerModFile> Files)
{
    public string Label => $"{Name} · {Players}/{MaximumPlayers} · {GameMode} · {Map} · {Ping} ms";
}
public sealed record MultiplayerFileMatch(ServerModFile Required, MultiplayerModFile? File, string? Path);

public static class MultiplayerService
{
    public const string DefaultMaster = "master.zandronum.com:15300";
    private const uint Flags = 1 | 8 | 16 | 32 | 64 | 128 | 512 | 1024 | 2048 | 0x80000 | 0x40000000 | 0x80000000;
    private static readonly HashSet<string> IwadNames = new(StringComparer.OrdinalIgnoreCase)
        { "doom.wad", "doom1.wad", "doom2.wad", "tnt.wad", "plutonia.wad", "heretic.wad", "heretic1.wad", "hexen.wad", "hexdd.wad", "strife1.wad", "voices.wad", "chex.wad", "chex3.wad", "hacx.wad", "freedoom1.wad", "freedoom2.wad", "freedm.wad" };
    public static bool IsProtectedIwad(string name) => IwadNames.Contains(name);

    public static async Task<IPEndPoint> ResolveAddressAsync(string value, int defaultPort = 10666, CancellationToken token = default)
    {
        value = value.Trim(); if (value.Length is 0 or > 255 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || value.IndexOfAny(['/', '\\', '"', '\'', ';', '+']) >= 0) throw new ArgumentException("Invalid server address.");
        var pieces = value.Split(':'); if (pieces.Length > 2) throw new ArgumentException("Use an IPv4 address or DNS name, optionally followed by :port.");
        var host = pieces[0]; if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) throw new ArgumentException("Invalid host name.");
        var port = pieces.Length == 2 && int.TryParse(pieces[1], out var parsed) ? parsed : pieces.Length == 1 ? defaultPort : 0;
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(value), "Invalid UDP port.");
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
        return new(addresses.FirstOrDefault() ?? throw new IOException("No IPv4 address found."), port);
    }

    public static IReadOnlyList<string> BuildLaunchArguments(string executable, MultiplayerLaunchRequest request)
    {
        if (request.Port is < 1 or > 65535 || request.Players is < 2 or > 64) throw new ArgumentException("Invalid multiplayer port or player count.");
        var engine = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        if (!SaveGameService.SupportsManagedSaves(executable)) throw new InvalidOperationException("This engine has no supported multiplayer adapter.");
        var zandronum = engine == "zandronum";
        if (request.Mode == "join")
        {
            var address = request.Address ?? "";
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Resolve the server to a valid IPv4 address before joining.");
            return [zandronum ? "-connect" : "-join", $"{ip}:{request.Port}"];
        }
        if (request.Mode != "host" || request.GameMode is not ("coop" or "deathmatch" or "teamplay")) throw new ArgumentException("Invalid multiplayer mode.");
        if (zandronum) return ["-host", "-port", request.Port.ToString(), "+sv_maxclients", request.Players.ToString(), "+sv_maxplayers", request.Players.ToString(), "+sv_updatemaster", "0", "+sv_broadcast", "1", "+" + (request.GameMode == "coop" ? "cooperative" : request.GameMode), "1"];
        return ["-host", request.Players.ToString(), "-port", request.Port.ToString(), "-netmode", "1", "+deathmatch", request.GameMode == "coop" ? "0" : "1", "+teamplay", request.GameMode == "teamplay" ? "1" : "0"];
    }

    public static async Task<IReadOnlyList<MultiplayerServer>> BrowseInternetAsync(string master, IProgress<string>? progress, CancellationToken token)
    {
        var endpoint = await ResolveAddressAsync(master, 15300, token); using var udp = new UdpClient(AddressFamily.InterNetwork); udp.Connect(endpoint);
        var query = new byte[6]; BinaryPrimitives.WriteUInt32LittleEndian(query, 5660028); BinaryPrimitives.WriteUInt16LittleEndian(query.AsSpan(4), 2);
        await udp.SendAsync(ZandronumHuffman.Encode(query), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var addresses = new HashSet<IPEndPoint>(); var packets = new HashSet<int>(); int? last = null;
        try
        {
            while (!last.HasValue || Enumerable.Range(0, last.Value + 1).Any(index => !packets.Contains(index)))
            {
                var data = await udp.ReceiveAsync(timeout.Token); var parsed = ParseMasterPacket(ZandronumHuffman.Decode(data.Buffer)); packets.Add(parsed.Part);
                foreach (var server in parsed.Servers) if (IsPublic(server.Address)) addresses.Add(server);
                if (addresses.Count > 4096) throw new InvalidDataException("Master list exceeds the server safety limit.");
                if (parsed.Last) last = parsed.Part;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Master-server query timed out or returned an incomplete list."); }
        var result = new ConcurrentBag<MultiplayerServer>(); var completed = 0;
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (address, ct) =>
        {
            try { result.Add(await QueryServerAsync(address, ct)); }
            catch (Exception error) when (error is IOException or SocketException or InvalidDataException or TimeoutException) { }
            progress?.Report($"{Interlocked.Increment(ref completed)} / {addresses.Count}");
        });
        return result.OrderByDescending(server => server.Players).ThenBy(server => server.Ping).ToArray();
    }

    private static bool IsPublic(IPAddress ip)
    {
        var b = ip.GetAddressBytes(); return b.Length == 4 && b[0] is not (0 or 10 or 127) && b[0] < 224
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    internal static (int Part, bool Last, IReadOnlyList<IPEndPoint> Servers) ParseMasterPacket(byte[] data)
    {
        var input = new Packet(data); var response = input.UInt32();
        if (response != 6) throw new InvalidDataException(response is 3 or 4 or 5 ? $"Master refused query ({response}); wait before retrying." : "Unsupported master response.");
        var part = input.Byte(); if (part > 63) throw new InvalidDataException("Too many master-list parts.");
        var code = input.Byte(); var result = new List<IPEndPoint>();
        if (code == 8)
        {
            var count = input.Byte();
            while (count != 0)
            {
                var ip = new IPAddress(input.Bytes(4));
                for (var i = 0; i < count; i++) { var port = input.UInt16(); if (port > 0) result.Add(new(ip, port)); }
                count = input.Byte();
            }
            code = input.Byte();
        }
        if (code is not (2 or 7)) throw new InvalidDataException("Invalid master-list terminator.");
        return (part, code == 2, result);
    }

    private static byte[] Query(uint nonce)
    {
        var data = new byte[17]; BinaryPrimitives.WriteUInt32LittleEndian(data, 199); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), nonce); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 5); data[16] = 2;
        return ZandronumHuffman.Encode(data);
    }

    public static async Task<IReadOnlyList<MultiplayerServer>> BrowseLanAsync(int port, CancellationToken token)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var nonce = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)); await udp.SendAsync(Query(nonce), new IPEndPoint(IPAddress.Broadcast, port), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var assemblies = new Dictionary<IPEndPoint, ServerReplyAssembler>(); var result = new Dictionary<IPEndPoint, MultiplayerServer>(); var clock = Stopwatch.StartNew();
        try
        {
            for (var packet = 0; packet < 2048; packet++)
            {
                var data = await udp.ReceiveAsync(timeout.Token);
                if (data.RemoteEndPoint.Port != port || result.ContainsKey(data.RemoteEndPoint)) continue;
                if (!assemblies.TryGetValue(data.RemoteEndPoint, out var assembler))
                { if (assemblies.Count == 256) continue; assemblies[data.RemoteEndPoint] = assembler = new(); }
                try
                { if (assembler.Add(data.Buffer) is { } payload) result[data.RemoteEndPoint] = ParseServerPayload(payload, data.RemoteEndPoint, nonce, clock.ElapsedMilliseconds); }
                catch (Exception error) when (error is IOException or InvalidDataException) { assemblies.Remove(data.RemoteEndPoint); }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return result.Values.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<MultiplayerServer> QueryServerAsync(IPEndPoint endpoint, CancellationToken token)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork); udp.Connect(endpoint); var clock = Stopwatch.StartNew();
        var nonce = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)); await udp.SendAsync(Query(nonce), token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var assembler = new ServerReplyAssembler();
        try
        {
            for (var packet = 0; packet < 256; packet++)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if (assembler.Add(response.Buffer) is { } payload) return ParseServerPayload(payload, endpoint, nonce, clock.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Server did not respond in time."); }
        throw new InvalidDataException("Server exceeded the packet limit.");
    }

    internal sealed class ServerReplyAssembler
    {
        private byte[]? _joined;
        private bool[]? _segments, _coverage;
        public byte[]? Add(byte[] packet)
        {
            var input = new Packet(ZandronumHuffman.Decode(packet)); var code = input.UInt32();
            if (code is 5660024 or 5660025) throw new IOException("Server query refused; wait before refreshing.");
            if (code == 5660023) return input.Bytes(input.Remaining);
            if (code != 5660032) throw new InvalidDataException("Unexpected server reply.");
            while (input.Remaining > 0)
            {
                var number = input.Byte() & 127; var count = input.Byte(); var offset = input.UInt16(); var size = input.UInt16(); var total = input.UInt16();
                if (count is 0 or > 128 || number >= count || size == 0 || offset + size > total) throw new InvalidDataException("Invalid server segment bounds.");
                _joined ??= new byte[total]; _segments ??= new bool[count]; _coverage ??= new bool[total];
                if (_joined.Length != total || _segments.Length != count) throw new InvalidDataException("Inconsistent server segments.");
                var bytes = input.Bytes(size);
                if (!_segments[number])
                {
                    for (var p = 0; p < size; p++) { if (_coverage[offset + p]) throw new InvalidDataException("Overlapping server segments."); _coverage[offset + p] = true; _joined[offset + p] = bytes[p]; }
                    _segments[number] = true;
                }
            }
            if (_segments is null || !_segments.All(value => value)) return null;
            if (_coverage!.Any(value => !value)) throw new InvalidDataException("Incomplete server data.");
            return _joined;
        }
    }

    internal static MultiplayerServer ParseServerPayload(byte[] data, IPEndPoint endpoint, uint nonce, long ping)
    {
        var input = new Packet(data); if (input.UInt32() != nonce) throw new InvalidDataException("Stale server reply.");
        var version = input.Text(); var flags = input.UInt32(); if ((flags & ~Flags) != 0) throw new InvalidDataException("Server returned unsolicited fields.");
        bool Has(uint bit) => (flags & bit) != 0;
        var name = Has(1) ? input.Text() : endpoint.ToString(); var map = Has(8) ? input.Text() : "";
        var maximum = Has(16) ? input.Byte() : 0; if (Has(32)) maximum = input.Byte();
        var files = new List<ServerModFile>();
        if (Has(64)) { var count = input.Byte(); for (var i = 0; i < count; i++) files.Add(new(ValidateFileName(input.Text()), null)); }
        var mode = ""; if (Has(128)) { mode = "mode " + input.Byte(); input.Bytes(2); }
        var iwad = Has(512) ? ValidateFileName(input.Text()) : "";
        var password = Has(1024) && input.Byte() != 0; if (Has(2048)) password |= input.Byte() != 0;
        var players = Has(0x80000) ? input.Byte() : 0;
        if (Has(0x40000000)) { var count = input.Byte(); for (var i = 0; i < count; i++) files.Add(new(ValidateFileName(input.Text()), null)); }
        if (Has(0x80000000))
        {
            var extended = input.UInt32(); if ((extended & ~5u) != 0) throw new InvalidDataException("Unsupported extended server fields.");
            if ((extended & 1) != 0)
            {
                var count = input.Byte(); if (count > files.Count) throw new InvalidDataException("Invalid server hash count.");
                for (var i = 0; i < count; i++) { var hash = input.Text(); if (hash.Length != 32 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid mod checksum."); files[i] = files[i] with { Md5 = hash.ToLowerInvariant() }; }
            }
            if ((extended & 4) != 0) mode = input.Text();
        }
        return new(endpoint, Clean(name), Clean(map), Clean(mode), iwad, Clean(version), players, maximum, password, ping, files);
    }

    public static string ValidateFileName(string name)
    {
        if (name.Length is 0 or > 180 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) throw new InvalidDataException("Server supplied an unsafe filename.");
        if (Path.GetExtension(name).ToLowerInvariant() is not (".wad" or ".pk3" or ".pk7" or ".pke" or ".deh" or ".bex" or ".zip" or ".7z" or ".rar")) throw new InvalidDataException("Unsupported server content type.");
        return name;
    }
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(512).ToArray());

    public static async Task<IReadOnlyList<MultiplayerFileMatch>> MatchFilesAsync(MultiplayerServer server,
        IReadOnlyList<(int GameId, string Path)> archives, CancellationToken token)
    {
        var result = new List<MultiplayerFileMatch>();
        foreach (var required in server.Files)
        {
            MultiplayerFileMatch? match = null;
            foreach (var candidate in archives)
            {
                token.ThrowIfCancellationRequested(); if (!File.Exists(candidate.Path)) continue;
                var ext = Path.GetExtension(candidate.Path).ToLowerInvariant();
                if (ext is ".wad" or ".deh" or ".bex" || ext == Path.GetExtension(required.Name).ToLowerInvariant() && Path.GetFileName(candidate.Path).Equals(required.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Path.GetFileName(candidate.Path).Equals(required.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    await using var input = File.OpenRead(candidate.Path);
                    if (required.Md5 is not null && !Convert.ToHexString(await MD5.HashDataAsync(input, token)).Equals(required.Md5, StringComparison.OrdinalIgnoreCase)) continue;
                    match = new(required, new(candidate.GameId, ""), candidate.Path); break;
                }
                try
                {
                    using var archive = ArchiveFactory.OpenArchive(candidate.Path);
                    var entry = archive.Entries.FirstOrDefault(entry => !entry.IsDirectory && Path.GetFileName(entry.Key ?? "").Equals(required.Name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null || entry.Size > 1024L * 1024 * 1024) continue;
                    if (required.Md5 is not null)
                    { using var input = entry.OpenEntryStream(); if (!Convert.ToHexString(await MD5.HashDataAsync(input, token)).Equals(required.Md5, StringComparison.OrdinalIgnoreCase)) continue; }
                    match = new(required, new(candidate.GameId, entry.Key!), candidate.Path); break;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException) { }
            }
            result.Add(match ?? new(required, null, null));
        }
        return result;
    }

    private sealed class Packet(byte[] bytes)
    {
        private int position;
        public int Remaining => bytes.Length - position;
        public byte Byte() { if (Remaining < 1) throw new InvalidDataException("Truncated query packet."); return bytes[position++]; }
        public byte[] Bytes(int count) { if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated query packet."); var result = bytes.AsSpan(position, count).ToArray(); position += count; return result; }
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public string Text()
        {
            var start = position; while (position < bytes.Length && bytes[position] != 0) { if (position - start > 2048) throw new InvalidDataException("Query string is too long."); position++; }
            if (position == bytes.Length) throw new InvalidDataException("Unterminated query string.");
            var result = Encoding.UTF8.GetString(bytes, start, position - start); position++; return result;
        }
    }
}
