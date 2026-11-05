using System.Text;
using TransportAnalyzer.Infrastructure;

namespace TransportAnalyzer.Tests;

public class RawStoreTests
{
    [Fact]
    public async Task RawStore_TwoSavesSameSecond_DoNotCollide()
    {
        var root = Path.Combine(Path.GetTempPath(), "ta-raw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RawStore(root);
            var paths = new List<string>();
            for (var i = 0; i < 20; i++)
                paths.Add((await store.SaveAsync("src", "a.csv", new MemoryStream(Encoding.UTF8.GetBytes("x")), CancellationToken.None)).Path);
            Assert.Equal(20, paths.Distinct().Count());
            Assert.All(paths, p => Assert.True(File.Exists(p)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
