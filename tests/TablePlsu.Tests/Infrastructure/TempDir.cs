using TablePlsu.Core.Paths;

namespace TablePlsu.Tests.Infrastructure;

/// <summary>A unique temp directory deleted on dispose, plus an <see cref="AppPaths"/> rooted in it.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tableplsu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Paths = AppPaths.ForRoot(Path);
    }

    public string Path { get; }
    public AppPaths Paths { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
