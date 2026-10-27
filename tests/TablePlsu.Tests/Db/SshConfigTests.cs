using TablePlsu.Core.Db;

namespace TablePlsu.Tests.Db;

public class SshConfigTests
{
    private const string Config = """
        # comment
        Host bastion prod-*
          HostName bastion.example.com
          User bob
          Port 2222
          IdentityFile ~/.ssh/id_work

        Host *
          User fallback
          IdentityFile ~/.ssh/id_other
        """;

    [Fact]
    public void Finds_host_with_first_value_winning_and_wildcards()
    {
        var e = SshConfig.Find("bastion", Config);
        Assert.NotNull(e);
        Assert.Equal("bastion.example.com", e!.HostName);
        Assert.Equal("bob", e.User);
        Assert.Equal(2222, e.Port);
        Assert.Equal(2, e.IdentityFiles.Count);
        Assert.EndsWith("id_work", e.IdentityFiles[0]);

        var wild = SshConfig.Find("prod-db1", Config);
        Assert.Equal("bastion.example.com", wild!.HostName);

        var other = SshConfig.Find("elsewhere", Config);
        Assert.Null(other!.HostName);
        Assert.Equal("fallback", other.User);
    }

    [Fact]
    public void No_match_returns_null() => Assert.Null(SshConfig.Find("x", "Host y\n  User z\n"));
}
