using System.Security.Cryptography;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Infrastructure;

/// <summary>Writes fetched source files to {root}/{sourceId}/{yyyyMMdd'T'HHmmssfff}/{name}; never overwrites.
/// A save landing in an already-used millisecond directory gets a -1, -2, ... suffix.</summary>
public sealed class RawStore(string root) : IRawStore
{
    public async Task<RawFile> SaveAsync(string sourceId, string name, Stream content, CancellationToken ct)
    {
        var fetchedAt = DateTimeOffset.UtcNow;
        var stamp = fetchedAt.ToString("yyyyMMdd'T'HHmmssfff");

        // CreateNew throws IOException if the file exists, so existing files are never overwritten.
        FileStream file;
        string path;
        for (var attempt = 0; ; attempt++)
        {
            var dir = Path.Combine(root, sourceId, attempt == 0 ? stamp : $"{stamp}-{attempt}");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, name);
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                break;
            }
            catch (IOException) when (File.Exists(path)) { }
        }

        using var sha = SHA256.Create();
        await using (file)
        await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(hashing, ct);
        }
        return new RawFile(name, path, Convert.ToHexStringLower(sha.Hash!), fetchedAt);
    }
}
