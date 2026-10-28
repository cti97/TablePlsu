namespace TablePlsu.Core.Connections;

/// <summary>
/// The connection form's "your data may be in X" hint. A user who connects to the maintenance database
/// (<c>postgres</c>, or the empty field, which means the same) sees no tables; when the server has exactly one other
/// user database, that is almost certainly where their data is.
/// </summary>
public static class DatabaseSuggestion
{
    /// <summary>The maintenance database every PostgreSQL server has.</summary>
    public const string MaintenanceDatabase = "postgres";

    /// <summary>Templates and cloud providers' internal databases (RDS, Azure, Cloud SQL, AlloyDB, DigitalOcean, …): never user data.</summary>
    public static IReadOnlySet<string> SystemDatabases { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        MaintenanceDatabase,
        "template0",
        "template1",
        "rdsadmin",
        "azure_maintenance",
        "azure_sys",
        "cloudsqladmin",
        "alloydbadmin",
        "alloydbmetadata",
        "_dodb",
    };

    /// <summary>True for <see cref="SystemDatabases"/>.</summary>
    public static bool IsSystemDatabase(string name) => SystemDatabases.Contains(name);

    /// <summary>
    /// The database to suggest, or null. Only when <paramref name="current"/> is empty or <c>postgres</c> and the
    /// server has exactly one database that is not a system database.
    /// </summary>
    public static string? Suggest(string? current, IReadOnlyList<string>? databases)
    {
        if (databases is null) return null;
        var typed = current?.Trim() ?? "";
        if (typed.Length > 0 && typed != MaintenanceDatabase) return null;
        string? only = null;
        foreach (var name in databases)
        {
            if (string.IsNullOrEmpty(name) || IsSystemDatabase(name) || name == only) continue;
            if (only is not null) return null;
            only = name;
        }
        return only;
    }
}
