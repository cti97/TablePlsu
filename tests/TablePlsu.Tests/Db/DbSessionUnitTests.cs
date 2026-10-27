using Npgsql;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Db;

namespace TablePlsu.Tests.Db;

public class DbSessionUnitTests
{
    [Fact]
    public void Connection_string_maps_profile_and_ssl()
    {
        var p = new ConnectionProfile
        {
            Host = "db", Port = 6543, User = "u", Database = "d",
            SslMode = SslModeOption.VerifyFull,
            SslClientCertFile = @"C:\c.crt", SslClientKeyFile = @"C:\c.key", SslRootCertFile = @"C:\ca.crt",
        };
        var cs = DbSession.BuildConnectionString(p, new ConnectionSecrets("pw", null, null), database: "other", commandTimeoutSeconds: 30);
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("db", b.Host);
        Assert.Equal(6543, b.Port);
        Assert.Equal("u", b.Username);
        Assert.Equal("pw", b.Password);
        Assert.Equal("other", b.Database);
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
        Assert.Equal(@"C:\c.crt", b.SslCertificate);
        Assert.Equal(@"C:\c.key", b.SslKey);
        Assert.Equal(@"C:\ca.crt", b.RootCertificate);
        Assert.Equal(30, b.CommandTimeout);
        Assert.Equal("TablePlsu", b.ApplicationName);
    }

    [Fact]
    public void Connection_string_uses_tunnel_endpoint_when_given()
    {
        var p = new ConnectionProfile { Host = "10.0.0.5", Port = 5432, User = "u", Database = "d" };
        var cs = DbSession.BuildConnectionString(p, new ConnectionSecrets(null, null, null), hostOverride: "127.0.0.1", portOverride: 50123);
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("127.0.0.1", b.Host);
        Assert.Equal(50123, b.Port);
        Assert.Equal("d", b.Database);
        Assert.Null(b.Password);
    }

    [Theory]
    [InlineData(SslModeOption.Disable, SslMode.Disable)]
    [InlineData(SslModeOption.Allow, SslMode.Allow)]
    [InlineData(SslModeOption.Prefer, SslMode.Prefer)]
    [InlineData(SslModeOption.Require, SslMode.Require)]
    [InlineData(SslModeOption.VerifyCA, SslMode.VerifyCA)]
    [InlineData(SslModeOption.VerifyFull, SslMode.VerifyFull)]
    public void Ssl_modes_map(SslModeOption option, SslMode expected)
    {
        var cs = DbSession.BuildConnectionString(new ConnectionProfile { SslMode = option }, new ConnectionSecrets(null, null, null));
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(cs).SslMode);
    }

    [Fact]
    public void Value_conversion_makes_values_display_friendly()
    {
        Assert.Null(DbValueConverter.ToDisplayValue(DBNull.Value));
        Assert.Null(DbValueConverter.ToDisplayValue(null));
        Assert.Equal(42, DbValueConverter.ToDisplayValue(42));
        Assert.Equal("\\x0aff10", DbValueConverter.ToDisplayValue(new byte[] { 0x0a, 0xff, 0x10 }));

        var big = new byte[10_000];
        var hex = (string)DbValueConverter.ToDisplayValue(big)!;
        Assert.True(hex.Length < 2 + DbValueConverter.MaxBinaryPreviewBytes * 2 + 50);
        Assert.Contains("10000 bytes", hex);

        Assert.Equal("{1,2,NULL}", DbValueConverter.ToDisplayValue(new int?[] { 1, 2, null }));
        Assert.Equal("{\"a b\",c}", DbValueConverter.ToDisplayValue(new[] { "a b", "c" }));
        Assert.Equal("{{1,2},{3,4}}", DbValueConverter.ToDisplayValue(new[,] { { 1, 2 }, { 3, 4 } }));
        Assert.Equal("{}", DbValueConverter.ToDisplayValue(Array.Empty<int>()));
    }
}
