using System.Text;
using TablePlsu.Core.Connections;
using TablePlsu.Tests.Infrastructure;

namespace TablePlsu.Tests.Connections;

public class SecretStoreTests
{
    [Fact]
    public void Set_get_and_persist()
    {
        using var tmp = new TempDir();
        var s = new SecretStore(tmp.Paths);
        s.Set("c1", SecretKind.DbPassword, "db-secret");
        s.Set("c1", SecretKind.SshPassword, "ssh-secret");
        s.Set("c1", SecretKind.SshPassphrase, "phrase");

        var again = new SecretStore(tmp.Paths);
        Assert.Equal("db-secret", again.Get("c1", SecretKind.DbPassword));
        Assert.Equal("ssh-secret", again.Get("c1", SecretKind.SshPassword));
        Assert.Equal("phrase", again.Get("c1", SecretKind.SshPassphrase));
        Assert.Null(again.Get("c2", SecretKind.DbPassword));
    }

    [Fact]
    public void File_is_encrypted()
    {
        using var tmp = new TempDir();
        var s = new SecretStore(tmp.Paths);
        s.Set("c1", SecretKind.DbPassword, "very-recognisable-secret");
        var bytes = File.ReadAllBytes(tmp.Paths.SecretsFile);
        Assert.DoesNotContain("very-recognisable-secret", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("very-recognisable-secret", Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void Null_removes_and_DeleteAll_clears_connection()
    {
        using var tmp = new TempDir();
        var s = new SecretStore(tmp.Paths);
        s.Set("c1", SecretKind.DbPassword, "a");
        s.Set("c1", SecretKind.SshPassword, "b");
        s.Set("c2", SecretKind.DbPassword, "c");
        s.Set("c1", SecretKind.DbPassword, null);
        Assert.Null(s.Get("c1", SecretKind.DbPassword));
        s.DeleteAll("c1");
        Assert.Null(new SecretStore(tmp.Paths).Get("c1", SecretKind.SshPassword));
        Assert.Equal("c", new SecretStore(tmp.Paths).Get("c2", SecretKind.DbPassword));
    }

    [Fact]
    public void Corrupt_file_is_treated_as_empty()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Roaming);
        File.WriteAllBytes(tmp.Paths.SecretsFile, [1, 2, 3]);
        var s = new SecretStore(tmp.Paths);
        Assert.Null(s.Get("c1", SecretKind.DbPassword));
        Assert.NotNull(s.LastError);
    }
}
