using TablePlsu.Core.Connections;

namespace TablePlsu.Tests.Connections;

public class DatabaseSuggestionTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("postgres")]
    [InlineData("  postgres ")]
    public void Suggests_the_only_user_database_when_on_the_maintenance_db(string? current)
    {
        Assert.Equal("shop", DatabaseSuggestion.Suggest(current, ["postgres", "shop"]));
    }

    [Fact]
    public void No_suggestion_when_another_database_is_typed()
    {
        Assert.Null(DatabaseSuggestion.Suggest("shop", ["postgres", "shop"]));
        Assert.Null(DatabaseSuggestion.Suggest("other", ["postgres", "shop"]));
        Assert.Null(DatabaseSuggestion.Suggest("Postgres", ["postgres", "shop"]));
    }

    [Fact]
    public void No_suggestion_with_zero_or_several_user_databases()
    {
        Assert.Null(DatabaseSuggestion.Suggest("", ["postgres"]));
        Assert.Null(DatabaseSuggestion.Suggest("", []));
        Assert.Null(DatabaseSuggestion.Suggest("", null));
        Assert.Null(DatabaseSuggestion.Suggest("postgres", ["postgres", "shop", "crm"]));
    }

    [Theory]
    [InlineData("rdsadmin")]
    [InlineData("azure_maintenance")]
    [InlineData("azure_sys")]
    [InlineData("cloudsqladmin")]
    [InlineData("alloydbadmin")]
    [InlineData("_dodb")]
    [InlineData("template0")]
    [InlineData("template1")]
    public void Cloud_admin_and_template_databases_do_not_count(string system)
    {
        Assert.Equal("shop", DatabaseSuggestion.Suggest("postgres", ["postgres", system, "shop"]));
        Assert.Null(DatabaseSuggestion.Suggest("postgres", ["postgres", system]));
    }

    [Fact]
    public void Azure_flexible_server_layout_suggests_the_app_database()
    {
        Assert.Equal("app", DatabaseSuggestion.Suggest("", ["app", "azure_maintenance", "azure_sys", "postgres"]));
    }

    [Fact]
    public void Duplicates_count_once()
    {
        Assert.Equal("shop", DatabaseSuggestion.Suggest("", ["shop", "postgres", "shop"]));
    }
}
