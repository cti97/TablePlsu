using System.Text;

namespace TablePlsu.Core.IO;

/// <summary>Crash-safe writes: write a temp file next to the target, flush, then <see cref="File.Replace(string,string,string?)"/> or move.</summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, Utf8NoBom.GetBytes(contents));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            Commit(temp, full);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void Commit(string temp, string target)
    {
        // Another writer may replace the target concurrently; retry briefly on sharing violations.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                    File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException && !Directory.Exists(target))
            {
                Thread.Sleep(5 + attempt * 5);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>Reads a text file, retrying briefly while another process holds it (e.g. an editor mid-save).</summary>
    public static string ReadAllTextShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10 && File.Exists(path))
            {
                Thread.Sleep(20);
            }
        }
    }
}
