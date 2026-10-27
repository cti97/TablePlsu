using Npgsql;
using TablePlsu.Core.Connections;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Infrastructure;

/// <summary>Integration-test gate: reads TABLEPLSU_TEST_PG and skips (returns false) when unset.</summary>
public static class TestPg
{
    public const string EnvVar = "TABLEPLSU_TEST_PG";

    public static string? ConnectionString => Environment.GetEnvironmentVariable(EnvVar);

    public static bool Available(ITestOutputHelper output)
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return true;
        output.WriteLine($"SKIPPED: {EnvVar} is not set.");
        return false;
    }

    public static (ConnectionProfile Profile, ConnectionSecrets Secrets) Profile()
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionString);
        var profile = new ConnectionProfile
        {
            Name = "test",
            Host = b.Host ?? "localhost",
            Port = b.Port,
            User = b.Username ?? "postgres",
            Database = b.Database ?? "postgres",
            PasswordMode = string.IsNullOrEmpty(b.Password) ? PasswordMode.None : PasswordMode.Store,
            SslMode = SslModeOption.Disable,
        };
        return (profile, new ConnectionSecrets(b.Password, null, null));
    }
}
