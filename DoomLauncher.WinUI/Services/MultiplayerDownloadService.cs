using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Archives;

namespace DoomLauncher.WinUI.Services;

public static class MultiplayerDownloadService
{
    private const long MaximumBytes = 1024L * 1024 * 1024;
    public static async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Use an HTTPS download URL without credentials.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false }; using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        for (var redirects = 0; redirects < 6; redirects++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location ?? throw new IOException("Download redirect has no destination."); uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("Unsafe download redirect."); continue;
            }
            response.EnsureSuccessStatusCode(); var total = response.Content.Headers.ContentLength;
            if (total > MaximumBytes) throw new IOException("Download exceeds the 1 GiB safety limit.");
            await using var input = await response.Content.ReadAsStreamAsync(token); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072]; long written = 0; int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            { written += count; if (written > MaximumBytes) throw new IOException("Download exceeds the 1 GiB safety limit."); await output.WriteAsync(buffer.AsMemory(0, count), token); if (total > 0) progress?.Report(100d * written / total.Value); }
            progress?.Report(100); return;
        }
        throw new IOException("Too many download redirects.");
    }

    public static async Task<string> VerifyAndPrepareAsync(string path, ServerModFile required, CancellationToken token)
    {
        MultiplayerService.ValidateFileName(required.Name);
        if (MultiplayerService.IsProtectedIwad(required.Name)) throw new InvalidDataException("IWAD downloads are not allowed.");
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Downloaded content is too large.");
        static async Task<string> Digest(Stream input, CancellationToken ct) => Convert.ToHexString(await MD5.HashDataAsync(input, ct)).ToLowerInvariant();
        var header = new byte[4]; await using (var input = File.OpenRead(path)) { await input.ReadExactlyAsync(header, token); }
        if (header.AsSpan().SequenceEqual("IWAD"u8)) throw new InvalidDataException("IWAD downloads are not allowed.");
        var direct = required.Md5 is not null;
        if (direct) { await using var input = File.OpenRead(path); direct = await Digest(input, token) == required.Md5; }
        var extension = Path.GetExtension(required.Name).ToLowerInvariant();
        if (header.AsSpan().SequenceEqual("PWAD"u8))
        {
            if (required.Md5 is not null && !direct) throw new InvalidDataException("Downloaded WAD checksum does not match the server.");
            if (extension != ".wad") throw new InvalidDataException("Downloaded content is not the required file type."); return path;
        }
        var packed = header.AsSpan(0, 2).SequenceEqual("PK"u8) || header.AsSpan().SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf }) || header.AsSpan().SequenceEqual("Rar!"u8);
        if (extension is ".deh" or ".bex" && !packed && (direct || required.Md5 is null))
        {
            var text = await File.ReadAllTextAsync(path, token);
            if (text.IndexOf('\0') >= 0 || !text.Contains("Patch File for DeHackEd", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid DeHackEd patch.");
            var zipped = path + ".zip"; using (var archive = ZipFile.Open(zipped, ZipArchiveMode.Create)) archive.CreateEntryFromFile(path, required.Name); return zipped;
        }
        using (var archive = ArchiveFactory.OpenArchive(path))
        {
            var entries = archive.Entries.Where(entry => !entry.IsDirectory).Take(20001).ToArray();
            if (entries.Length > 20000 || entries.Sum(entry => Math.Max(0, entry.Size)) > 4 * MaximumBytes) throw new InvalidDataException("Downloaded archive exceeds extraction limits.");
            foreach (var entry in entries.Where(entry => Path.GetExtension(entry.Key ?? "").Equals(".wad", StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested(); using var stream = entry.OpenEntryStream(); var magic = new byte[4]; await stream.ReadExactlyAsync(magic, token);
                if (MultiplayerService.IsProtectedIwad(Path.GetFileName(entry.Key ?? "")) || magic.AsSpan().SequenceEqual("IWAD"u8)) throw new InvalidDataException("The archive contains base-game data; automatic IWAD downloads are not allowed.");
            }
            var requiredEntry = entries.FirstOrDefault(entry => Path.GetFileName(entry.Key ?? "").Equals(required.Name, StringComparison.OrdinalIgnoreCase));
            if (requiredEntry is not null)
            {
                if (required.Md5 is not null) { using var input = requiredEntry.OpenEntryStream(); if (await Digest(input, token) != required.Md5) throw new InvalidDataException("Downloaded mod checksum does not match the server."); }
                // A ZIP wrapper must stay an archive rather than being mistaken for a directly loadable PK3.
                var wrapped = path + ".zip"; File.Copy(path, wrapped, false); return wrapped;
            }
            if (required.Md5 is not null && !direct) throw new InvalidDataException("Downloaded archive does not match the server or contain the required file.");
            if (extension is not (".pk3" or ".pk7" or ".pke" or ".zip")) throw new InvalidDataException("Required file was not found inside the archive.");
        }
        return path;
    }
}
