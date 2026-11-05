using System.Security.Cryptography;
using TransportAnalyzer.Domain;

namespace TransportAnalyzer.Infrastructure;

/// <summary>Writes fetched source files to {root}/{sourceId}/{yyyyMMdd'T'HHmmss}/{name}; never overwrites.</summary>
public sealed class RawStore(string root) : IRawStore
{
    public async Task<RawFile> SaveAsync(string sourceId, string name, Stream content, CancellationToken ct)
    {
        var fetchedAt = DateTimeOffset.UtcNow;
        var dir = Path.Combine(root, sourceId, fetchedAt.ToString("yyyyMMdd'T'HHmmss"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);

        using var sha = SHA256.Create();
        // CreateNew throws IOException if the file exists, so existing files are never overwritten.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(hashing, ct);
        }
        return new RawFile(name, path, Convert.ToHexStringLower(sha.Hash!), fetchedAt);
    }
}
