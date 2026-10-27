using TablePlsu.Core.Connections;

namespace TablePlsu.Tests.Connections;

public class ConnectionUrlTests
{
    [Fact]
    public void Parses_basic_url()
    {
        var r = ConnectionUrl.Parse("postgresql://alice:s3cret@db.example.com:6543/shop");
        Assert.Equal("db.example.com", r.Profile.Host);
        Assert.Equal(6543, r.Profile.Port);
        Assert.Equal("alice", r.Profile.User);
        Assert.Equal("shop", r.Profile.Database);
        Assert.Equal("s3cret", r.Password);
        Assert.Equal(PasswordMode.Store, r.Profile.PasswordMode);
        Assert.False(r.Profile.SshEnabled);
    }

    [Fact]
    public void Postgres_scheme_and_defaults()
    {
        var r = ConnectionUrl.Parse("postgres://localhost");
        Assert.Equal("localhost", r.Profile.Host);
        Assert.Equal(5432, r.Profile.Port);
        Assert.Equal("postgres", r.Profile.User);
        Assert.Equal("postgres", r.Profile.Database);
        Assert.Null(r.Password);
        Assert.Equal(PasswordMode.None, r.Profile.PasswordMode);
    }

    [Fact]
    public void Decodes_percent_encoding()
    {
        var r = ConnectionUrl.Parse("postgresql://us%40er:p%40ss%3Aw%2Ford@host/my%20db");
        Assert.Equal("us@er", r.Profile.User);
        Assert.Equal("p@ss:w/ord", r.Password);
        Assert.Equal("my db", r.Profile.Database);
    }

    [Fact]
    public void Parses_ipv6_host()
    {
        var r = ConnectionUrl.Parse("postgresql://u@[::1]:5433/db");
        Assert.Equal("::1", r.Profile.Host);
        Assert.Equal(5433, r.Profile.Port);
    }

    [Fact]
    public void Parses_query_options()
    {
        var r = ConnectionUrl.Parse("postgresql://u@h/db?sslmode=verify-full&name=Prod%20DB&statusColor=%23FF0000&env=production&safeModeLevel=2&schema=sales&table=orders");
        Assert.Equal(SslModeOption.VerifyFull, r.Profile.SslMode);
        Assert.Equal("Prod DB", r.Profile.Name);
        Assert.Equal("#FF0000", r.Profile.Color);
        Assert.Equal("production", r.Profile.Tag);
        Assert.Equal(SafeModeLevel.Alert2, r.Profile.SafeMode);
        Assert.Equal("sales", r.Options["schema"]);
        Assert.Equal("orders", r.Options["table"]);
    }

    [Theory]
    [InlineData("disable", SslModeOption.Disable)]
    [InlineData("allow", SslModeOption.Allow)]
    [InlineData("prefer", SslModeOption.Prefer)]
    [InlineData("require", SslModeOption.Require)]
    [InlineData("verify-ca", SslModeOption.VerifyCA)]
    [InlineData("verify-full", SslModeOption.VerifyFull)]
    public void Ssl_modes(string text, SslModeOption expected) =>
        Assert.Equal(expected, ConnectionUrl.Parse($"postgresql://h/db?sslmode={text}").Profile.SslMode);

    [Fact]
    public void Status_color_without_hash_and_tag_alias()
    {
        var r = ConnectionUrl.Parse("postgresql://h/db?statusColor=00ff00&tag=staging&safeModeLevel=safe1");
        Assert.Equal("#00FF00", r.Profile.Color);
        Assert.Equal("staging", r.Profile.Tag);
        Assert.Equal(SafeModeLevel.Safe1, r.Profile.SafeMode);
    }

    [Fact]
    public void Parses_ssh_variant()
    {
        var r = ConnectionUrl.Parse("postgresql+ssh://bob:sshpw@bastion.example.com:2222/alice:dbpw@10.0.0.5:5432/shop?sslmode=require");
        Assert.True(r.Profile.SshEnabled);
        Assert.Equal("bastion.example.com", r.Profile.SshHost);
        Assert.Equal(2222, r.Profile.SshPort);
        Assert.Equal("bob", r.Profile.SshUser);
        Assert.Equal("sshpw", r.SshPassword);
        Assert.Equal(PasswordMode.Store, r.Profile.SshPasswordMode);
        Assert.Equal("10.0.0.5", r.Profile.Host);
        Assert.Equal("alice", r.Profile.User);
        Assert.Equal("dbpw", r.Password);
        Assert.Equal("shop", r.Profile.Database);
        Assert.Equal(SslModeOption.Require, r.Profile.SslMode);
    }

    [Fact]
    public void Ssh_variant_defaults_port_22_and_no_password_means_key()
    {
        var r = ConnectionUrl.Parse("postgresql+ssh://bob@bastion/alice@db/shop");
        Assert.Equal(22, r.Profile.SshPort);
        Assert.Null(r.SshPassword);
        Assert.True(r.Profile.SshUseKey);
        Assert.Equal("db", r.Profile.Host);
        Assert.Equal(5432, r.Profile.Port);
    }

    [Theory]
    [InlineData("mysql://h/db")]
    [InlineData("postgresql://")]
    [InlineData("postgresql://h:notaport/db")]
    [InlineData("not a url")]
    [InlineData("postgresql+ssh://bastion")]
    public void Invalid_urls_fail(string url)
    {
        Assert.False(ConnectionUrl.TryParse(url, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => ConnectionUrl.Parse(url));
    }

    [Fact]
    public void Format_round_trips()
    {
        var p = new ConnectionProfile
        {
            Name = "Prod DB",
            Host = "db.example.com",
            Port = 6543,
            User = "us@er",
            Database = "my db",
            SslMode = SslModeOption.Require,
            Color = "#FF0000",
            Tag = "production",
            SafeMode = SafeModeLevel.Safe2,
        };
        var url = ConnectionUrl.Format(p, password: "p@ss:w/ord");
        Assert.StartsWith("postgresql://us%40er:p%40ss%3Aw%2Ford@db.example.com:6543/my%20db?", url);
        var r = ConnectionUrl.Parse(url);
        Assert.Equal(p.Host, r.Profile.Host);
        Assert.Equal(p.Port, r.Profile.Port);
        Assert.Equal(p.User, r.Profile.User);
        Assert.Equal(p.Database, r.Profile.Database);
        Assert.Equal(p.SslMode, r.Profile.SslMode);
        Assert.Equal(p.Name, r.Profile.Name);
        Assert.Equal(p.Color, r.Profile.Color);
        Assert.Equal(p.Tag, r.Profile.Tag);
        Assert.Equal(p.SafeMode, r.Profile.SafeMode);
        Assert.Equal("p@ss:w/ord", r.Password);
    }

    [Fact]
    public void Format_without_password_and_default_options_is_minimal()
    {
        var p = new ConnectionProfile { Host = "h", Port = 5432, User = "u", Database = "d", SslMode = SslModeOption.Prefer, Name = "" };
        Assert.Equal("postgresql://u@h:5432/d", ConnectionUrl.Format(p));
    }

    [Fact]
    public void Format_ssh_round_trips()
    {
        var p = new ConnectionProfile
        {
            Host = "10.0.0.5", User = "alice", Database = "shop",
            SshEnabled = true, SshHost = "bastion", SshPort = 2222, SshUser = "bob",
        };
        var url = ConnectionUrl.Format(p, password: "dbpw", sshPassword: "sshpw");
        Assert.Equal("postgresql+ssh://bob:sshpw@bastion:2222/alice:dbpw@10.0.0.5:5432/shop", url);
        var r = ConnectionUrl.Parse(url);
        Assert.Equal("sshpw", r.SshPassword);
        Assert.Equal("dbpw", r.Password);
        Assert.Equal("bastion", r.Profile.SshHost);
    }

    [Fact]
    public void Format_ipv6_host_uses_brackets()
    {
        var p = new ConnectionProfile { Host = "::1", User = "u", Database = "d" };
        Assert.Equal("postgresql://u@[::1]:5432/d", ConnectionUrl.Format(p));
    }
}
