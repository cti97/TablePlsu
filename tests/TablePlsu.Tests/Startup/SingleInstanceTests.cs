using System.Diagnostics;
using TablePlsu.Core.Startup;

namespace TablePlsu.Tests.Startup;

public class LaunchArgsTests
{
    [Fact]
    public void Parses_flags_and_connection_url()
    {
        var a = LaunchArgs.Parse(["--BACKGROUND", "postgresql://u:p@h:5432/db?sslmode=disable", "--other"]);
        Assert.True(a.Background);
        Assert.False(a.MeasureStartup);
        Assert.False(a.Quit);
        Assert.Equal("postgresql://u:p@h:5432/db?sslmode=disable", a.Url);
    }

    [Theory]
    [InlineData("postgres://h/db")]
    [InlineData("postgresql+ssh://s@sh/u@h/db")]
    [InlineData("PostgreSQL://h/db")]
    public void Recognises_every_url_scheme(string url) => Assert.Equal(url, LaunchArgs.Parse([url]).Url);

    [Fact]
    public void No_args_is_a_plain_launch()
    {
        var a = LaunchArgs.Parse([]);
        Assert.False(a.Background);
        Assert.Null(a.Url);
        Assert.True(a.IsPlainLaunch);
        Assert.True(LaunchArgs.Parse(["--measure-startup"]).MeasureStartup);
        Assert.True(LaunchArgs.Parse(["--quit"]).Quit);
        Assert.True(LaunchArgs.Parse(["--measure-activation"]).MeasureActivation);
    }
}

public class SingleInstanceTests
{
    private static string UniqueName() => SingleInstance.NameFor("test-user", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    [Fact]
    public void Name_is_per_user_and_per_data_root()
    {
        var a = SingleInstance.NameFor("alice", @"C:\Users\alice\AppData\Roaming\TablePlsu");
        Assert.Equal(a, SingleInstance.NameFor("alice", @"c:\users\alice\appdata\roaming\tableplsu\"));
        Assert.NotEqual(a, SingleInstance.NameFor("bob", @"C:\Users\alice\AppData\Roaming\TablePlsu"));
        Assert.NotEqual(a, SingleInstance.NameFor("alice", @"D:\portable\TablePlsu"));
        Assert.StartsWith("TablePlsu-", a);
    }

    [Fact]
    public void Second_acquire_is_not_primary_until_the_first_is_released()
    {
        var name = UniqueName();
        // Ownership is "who created the named mutex", not thread ownership, so it works from any thread and a crashed
        // primary (handle closed by the OS) frees the name for the next launch.
        SingleInstance? a = null, b = null;
        RunOn(() => a = SingleInstance.Acquire(name));
        RunOn(() => b = SingleInstance.Acquire(name));
        Assert.True(a!.IsPrimary);
        Assert.False(b!.IsPrimary);
        b.Dispose();
        a.Dispose();
        using var c = SingleInstance.Acquire(name);
        Assert.True(c.IsPrimary);
    }

    [Fact]
    public async Task Forwarded_args_reach_the_listener_quickly()
    {
        var name = UniqueName();
        using var primary = SingleInstance.Acquire(name);
        var received = new TaskCompletionSource<LaunchMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(m => received.TrySetResult(m));

        var sw = Stopwatch.StartNew();
        var sent = SingleInstance.TrySend(name, new LaunchMessage(["postgresql://h/db", "--x"], @"C:\work"), TimeSpan.FromSeconds(5));
        sw.Stop();
        Assert.True(sent);
        var m = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["postgresql://h/db", "--x"], m.Args);
        Assert.Equal(@"C:\work", m.WorkingDirectory);
        // The second process must be gone in < 100 ms; the hand-off itself is a small fraction of that.
        Assert.True(sw.ElapsedMilliseconds < 1000, $"hand-off took {sw.ElapsedMilliseconds} ms");

        // Several launches in a row are all delivered (the server re-arms after each client).
        for (var i = 0; i < 3; i++)
            Assert.True(SingleInstance.TrySend(name, new LaunchMessage([$"#{i}"], null), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Send_fails_fast_when_nobody_listens()
    {
        var sw = Stopwatch.StartNew();
        Assert.False(SingleInstance.TrySend(UniqueName(), new LaunchMessage([], null), TimeSpan.FromMilliseconds(200)));
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public void Message_round_trips_through_the_wire_format()
    {
        var msg = new LaunchMessage(["a b", "", "ünïcødé\nline"], null);
        using var ms = new MemoryStream();
        LaunchMessage.Write(ms, msg);
        ms.Position = 0;
        var back = LaunchMessage.Read(ms);
        Assert.Equal(msg.Args, back.Args);
        Assert.Null(back.WorkingDirectory);
    }

    private static void RunOn(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
