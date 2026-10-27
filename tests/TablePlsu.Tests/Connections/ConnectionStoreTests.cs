using TablePlsu.Core.Connections;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Connections;

public class ConnectionStoreTests
{
    [Fact]
    public void Empty_when_no_file()
    {
        using var tmp = new TempDir();
        var store = new ConnectionStore(tmp.Paths);
        store.Load();
        Assert.Empty(store.Profiles);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Save_persists_and_reloads_all_fields()
    {
        using var tmp = new TempDir();
        var store = new ConnectionStore(tmp.Paths);
        var p = new ConnectionProfile
        {
            Name = "Local",
            Color = "#00AA00",
            Tag = "local",
            SafeMode = SafeModeLevel.Alert1,
            Host = "localhost",
            Port = 5433,
            User = "me",
            Database = "app",
            PasswordMode = PasswordMode.Ask,
            PasswordCommand = "echo hi",
            SslMode = SslModeOption.VerifyCA,
            SslClientKeyFile = @"C:\k.key",
            SslClientCertFile = @"C:\c.crt",
            SslRootCertFile = @"C:\ca.crt",
            SshEnabled = true,
            SshHost = "bastion",
            SshPort = 2222,
            SshUser = "bob",
            SshPasswordMode = PasswordMode.None,
            SshUseKey = true,
            SshKeyFile = @"C:\id_ed25519",
            Bootstrap = "SET search_path TO app;",
            RowLimit = 500,
        };
        store.Save(p);

        var again = new ConnectionStore(tmp.Paths);
        again.Load();
        var loaded = Assert.Single(again.Profiles);
        Assert.Equal(p, loaded);
    }

    [Fact]
    public void Save_upserts_by_id_and_keeps_order()
    {
        using var tmp = new TempDir();
        var store = new ConnectionStore(tmp.Paths);
        var a = new ConnectionProfile { Name = "A" };
        var b = new ConnectionProfile { Name = "B" };
        store.Save(a);
        store.Save(b);
        store.Save(a with { Name = "A2" });
        Assert.Equal(new[] { "A2", "B" }, store.Profiles.Select(x => x.Name));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Delete_removes_profile_and_secrets()
    {
        using var tmp = new TempDir();
        var secrets = new SecretStore(tmp.Paths);
        var store = new ConnectionStore(tmp.Paths, secrets);
        var a = new ConnectionProfile { Name = "A" };
        store.Save(a);
        secrets.Set(a.Id, SecretKind.DbPassword, "pw");
        store.Delete(a.Id);
        Assert.Empty(store.Profiles);
        Assert.Null(secrets.Get(a.Id, SecretKind.DbPassword));
    }

    [Fact]
    public void Duplicate_copies_settings_and_secrets_with_new_id()
    {
        using var tmp = new TempDir();
        var secrets = new SecretStore(tmp.Paths);
        var store = new ConnectionStore(tmp.Paths, secrets);
        var a = new ConnectionProfile { Name = "A", Host = "h", Bootstrap = "SET x=1" };
        store.Save(a);
        secrets.Set(a.Id, SecretKind.DbPassword, "pw");
        var copy = store.Duplicate(a.Id);
        Assert.NotEqual(a.Id, copy.Id);
        Assert.Equal("A copy", copy.Name);
        Assert.Equal("h", copy.Host);
        Assert.Equal("SET x=1", copy.Bootstrap);
        Assert.Equal(2, store.Profiles.Count);
        Assert.Equal("pw", secrets.Get(copy.Id, SecretKind.DbPassword));
    }

    [Fact]
    public void Corrupt_file_reports_error_and_does_not_throw()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllText(tmp.Paths.ConnectionsFile, "{ nope");
        var store = new ConnectionStore(tmp.Paths);
        store.Load();
        Assert.Empty(store.Profiles);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void Get_by_id()
    {
        using var tmp = new TempDir();
        var store = new ConnectionStore(tmp.Paths);
        var a = new ConnectionProfile { Name = "A" };
        store.Save(a);
        Assert.Equal(a, store.Get(a.Id));
        Assert.Null(store.Get("missing"));
    }
}
