using GeoAutoDocs.Infrastructure;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class LocalLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GeoAutoDocsLoggerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RotatesLogAfterSizeLimit()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "GeoAutoDocs.log");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(5 * 1024 * 1024);

        new LocalLogger(path).Info("novo log");

        Assert.True(File.Exists(path + ".1"));
        Assert.Contains("novo log", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
