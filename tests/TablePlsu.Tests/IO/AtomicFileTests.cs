using TablePlsu.Core.IO;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.IO;

public class AtomicFileTests
{
    [Fact]
    public void Writes_new_file_and_creates_directory()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "a", "b", "f.txt");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void Replaces_existing_file_and_leaves_no_temp_files()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "f.txt");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(tmp.Path));
    }

    [Fact]
    public void Writes_utf8_without_bom()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "f.txt");
        AtomicFile.WriteAllText(path, "ä");
        Assert.Equal(new byte[] { 0xC3, 0xA4 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllBytes_round_trips()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "f.bin");
        AtomicFile.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Concurrent_writes_do_not_throw()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "f.txt");
        Parallel.For(0, 20, i => AtomicFile.WriteAllText(path, "v" + i));
        Assert.StartsWith("v", File.ReadAllText(path));
    }
}
