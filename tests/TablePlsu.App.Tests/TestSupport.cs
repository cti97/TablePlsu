using System.IO;
using System.Windows;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Connections;
using TablePlsu.Core.Paths;
using Xunit.Abstractions;

namespace TablePlsu.App.Tests;

/// <summary>Tests that touch the static AppServices run in this collection (never in parallel with each other).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppServicesCollection
{
    public const string Name = "AppServices";
}

/// <summary>A minimal tab for workspace tests.</summary>
public sealed class FakeTab : TabViewModelBase
{
    public FakeTab(string kind, string? schema = null, string? name = null) : base(kind)
    {
        Schema = schema;
        Name = name;
        Title = name ?? kind;
    }

    public int ReloadCount { get; private set; }
    public int SaveCount { get; private set; }
    public int CommitCount { get; private set; }
    public List<TabCloseReason> ClosedReasons { get; } = [];
    public bool RefuseClose { get; set; }

    public override FrameworkElement CreateView() => throw new NotSupportedException("No views in unit tests.");
    public override void Reload() => ReloadCount++;
    public override void Save() => SaveCount++;
    public override Task CommitAsync()
    {
        CommitCount++;
        return Task.CompletedTask;
    }
    public override bool CanCloseNow(TabCloseReason reason) => !RefuseClose;
    public override void OnClosed(TabCloseReason reason) => ClosedReasons.Add(reason);
}

/// <summary>Registers fake tab factories for object kinds and (optionally) SQL editors.</summary>
public sealed class FakeTabsFeature(bool withSqlEditor) : IFeature
{
    public void Register(FeatureContext ctx)
    {
        foreach (var kind in new[] { TabKinds.Table, TabKinds.View, TabKinds.MaterializedView, TabKinds.Function })
            ctx.Tabs.Register(kind, r => new FakeTab(r.Kind, r.Schema, r.Name));
        if (withSqlEditor) ctx.Tabs.Register(TabKinds.SqlEditor, r => new FakeTab(TabKinds.SqlEditor, null, r.Title ?? "Query"));
    }
}

public static class TestApp
{
    /// <summary>Initializes AppServices on a fresh temp root with the given features (default: workspace + fake tabs).</summary>
    public static string Init(params IFeature[] features)
    {
        var root = Path.Combine(Path.GetTempPath(), "tableplsu-app-tests", Guid.NewGuid().ToString("N"));
        AppServices.Init(AppPaths.ForRoot(root), features.Length > 0 ? features : [new WorkspaceFeature(), new FakeTabsFeature(withSqlEditor: true)]);
        return root;
    }

    public static ConnectionProfile Profile() => new() { Name = "test", Host = "localhost", Port = 1, Database = "db" };
}

/// <summary>Integration gate: TABLEPLSU_TEST_PG (an Npgsql connection string). Returns false (skips) when unset.</summary>
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
        var b = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString);
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
