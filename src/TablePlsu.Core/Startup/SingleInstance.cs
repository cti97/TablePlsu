using System.IO.Pipes;
using System.Text;

namespace TablePlsu.Core.Startup;

/// <summary>What a second launch hands to the running instance: its command-line args and working directory.</summary>
public sealed record LaunchMessage(IReadOnlyList<string> Args, string? WorkingDirectory)
{
    private const int Magic = 0x31_4C_50_54; // "TPL1"

    /// <summary>Wire format: magic, cwd (7-bit length-prefixed UTF-8, "\0" = none), count, args. No JSON, no reflection.</summary>
    public static void Write(Stream stream, LaunchMessage message)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(message.WorkingDirectory ?? "\0");
        w.Write(message.Args.Count);
        foreach (var a in message.Args) w.Write(a);
        w.Flush();
    }

    public static LaunchMessage Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != Magic) throw new InvalidDataException("Not a TablePlsu launch message.");
        var cwd = r.ReadString();
        var count = r.ReadInt32();
        if (count is < 0 or > 1024) throw new InvalidDataException("Bad argument count.");
        var args = new string[count];
        for (var i = 0; i < count; i++) args[i] = r.ReadString();
        return new LaunchMessage(args, cwd == "\0" ? null : cwd);
    }
}

/// <summary>
/// Single-instance gate: a named mutex decides which process is primary (whoever created it; the OS frees the name when
/// that process exits or crashes), and a named pipe (<see cref="PipeOptions.CurrentUserOnly"/>) carries a second launch's
/// args to the primary, so the second process can exit within milliseconds. Names are per user and per data root, so a
/// portable/test root (<c>TABLEPLSU_ROOT</c>) never talks to the user's normal instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _name;
    private CancellationTokenSource? _listen;

    private SingleInstance(Mutex mutex, string name, bool isPrimary)
    {
        _mutex = mutex;
        _name = name;
        IsPrimary = isPrimary;
    }

    /// <summary>True for the first process (the one that listens); false when another instance already runs.</summary>
    public bool IsPrimary { get; }

    public string Name => _name;

    /// <summary>"TablePlsu-" + 64-bit FNV-1a of user + normalised data root (no crypto: this runs before every launch).</summary>
    public static string NameFor(string user, string dataRoot)
    {
        var root = dataRoot.Trim().TrimEnd('\\', '/').ToUpperInvariant();
        var text = user.ToUpperInvariant() + "|" + root;
        var hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return "TablePlsu-" + hash.ToString("x16");
    }

    /// <summary>Opens (or creates) the instance mutex. Dispose closes the handle; the primary keeps it for its lifetime.</summary>
    public static SingleInstance Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out var createdNew);
        return new SingleInstance(mutex, name, createdNew);
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the primary and waits for its one-byte acknowledgement. False when no primary
    /// answered within <paramref name="timeout"/> (it may still be starting, hung, or exiting): the caller then starts normally.
    /// </summary>
    public static bool TrySend(string name, LaunchMessage message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Max(1, timeout.TotalMilliseconds));
            LaunchMessage.Write(client, message);
            return client.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Primary only: serves launch messages on a background task until disposed. <paramref name="onMessage"/> runs on a
    /// thread-pool thread (marshal to the UI yourself); it should return quickly because the sender waits for the ack.
    /// </summary>
    public void StartListening(Action<LaunchMessage> onMessage)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance listens.");
        if (_listen is not null) return;
        _listen = new CancellationTokenSource();
        var ct = _listen.Token;
        Task.Run(() => ListenLoop(onMessage, ct), ct);
    }

    private async Task ListenLoop(Action<LaunchMessage> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                // Name squatted or out of instances: back off and try again.
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                continue;
            }

            // Serve this client on its own task and re-arm immediately, so back-to-back launches never wait.
            _ = Task.Run(async () =>
            {
                await using var s = server;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var message = await ReadMessageAsync(s, timeout.Token).ConfigureAwait(false);
                    try { onMessage(message); }
                    catch (Exception) { /* the handler logs its own failures; the sender still gets its ack */ }
                    s.WriteByte(1);
                    s.Flush();
                }
                catch (Exception)
                {
                    // Malformed, timed out or disconnected client: drop it.
                }
            }, CancellationToken.None);
        }
    }

    private static async Task<LaunchMessage> ReadMessageAsync(Stream s, CancellationToken ct)
    {
        // Read the whole message into memory (async, cancellable), then parse synchronously.
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n == 0) break;
            buffer.Write(chunk, 0, n);
            buffer.Position = 0;
            try
            {
                var msg = LaunchMessage.Read(buffer);
                return msg;
            }
            catch (EndOfStreamException)
            {
                buffer.Position = buffer.Length; // incomplete: keep reading
            }
        }
        throw new EndOfStreamException();
    }

    public void Dispose()
    {
        _listen?.Cancel();
        _listen?.Dispose();
        _listen = null;
        _mutex.Dispose();
    }
}
