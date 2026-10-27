using TablePlsu.Core.Connections;
using TablePlsu.Core.Data;

namespace TablePlsu.Tests.Data;

public class SafeModePolicyTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("  -- comment\n select * from t")]
    [InlineData("/* x */ SELECT * FROM t FOR UPDATE")]
    [InlineData("select * from t for no key update")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("EXPLAIN DELETE FROM t")]
    [InlineData("EXPLAIN ANALYZE SELECT 1")]
    [InlineData("explain (analyze, buffers) select 1")]
    [InlineData("SHOW search_path")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("VALUES (1), (2)")]
    [InlineData("TABLE customers")]
    [InlineData("SELECT 'delete from t' AS s, \"insert\" FROM t")]
    [InlineData("SELECT $$ update t set a = 1 $$")]
    [InlineData("")]
    public void Read_only(string sql) => Assert.True(SafeModePolicy.IsReadOnlyStatement(sql));

    [Theory]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("insert into t values (1)")]
    [InlineData("DELETE FROM t")]
    [InlineData("SELECT * INTO new_t FROM t")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t")]
    [InlineData("explain (analyze) update t set a = 1")]
    [InlineData("CREATE TABLE t(a int)")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE t")]
    [InlineData("SET search_path = x")]
    [InlineData("DO $$ BEGIN END $$")]
    [InlineData("CALL p()")]
    [InlineData("VACUUM")]
    public void Writes(string sql) => Assert.False(SafeModePolicy.IsReadOnlyStatement(sql));

    [Fact]
    public void Multi_statement_text_is_read_only_only_when_all_are()
    {
        Assert.True(SafeModePolicy.AllReadOnly(["select 1; select 2"]));
        Assert.False(SafeModePolicy.AllReadOnly(["select 1; delete from t"]));
        Assert.False(SafeModePolicy.AllReadOnly(["select 1", "update t set a=1"]));
    }

    [Theory]
    [InlineData(SafeModeLevel.Silent, "delete from t", SafeModeRequirement.None)]
    [InlineData(SafeModeLevel.Alert1, "select 1", SafeModeRequirement.Confirm)]
    [InlineData(SafeModeLevel.Alert2, "select 1", SafeModeRequirement.None)]
    [InlineData(SafeModeLevel.Alert2, "delete from t", SafeModeRequirement.Confirm)]
    [InlineData(SafeModeLevel.Safe1, "select 1", SafeModeRequirement.Password)]
    [InlineData(SafeModeLevel.Safe2, "select 1", SafeModeRequirement.None)]
    [InlineData(SafeModeLevel.Safe2, "update t set a = 1", SafeModeRequirement.Password)]
    public void Evaluate_levels(SafeModeLevel level, string sql, SafeModeRequirement expected) =>
        Assert.Equal(expected, SafeModePolicy.Evaluate(level, [sql]));

    [Fact]
    public void Nothing_to_send_needs_nothing() => Assert.Equal(SafeModeRequirement.None, SafeModePolicy.Evaluate(SafeModeLevel.Safe1, []));

    [Theory]
    [InlineData(SafeModeLevel.Safe1, SafeModeLevel.Safe2, true)]
    [InlineData(SafeModeLevel.Safe1, SafeModeLevel.Silent, true)]
    [InlineData(SafeModeLevel.Safe2, SafeModeLevel.Alert1, true)]
    [InlineData(SafeModeLevel.Safe2, SafeModeLevel.Safe1, false)]
    [InlineData(SafeModeLevel.Alert1, SafeModeLevel.Silent, false)]
    [InlineData(SafeModeLevel.Silent, SafeModeLevel.Safe1, false)]
    public void Lowering_from_safe_requires_password(SafeModeLevel from, SafeModeLevel to, bool expected) =>
        Assert.Equal(expected, SafeModePolicy.ChangeRequiresPassword(from, to));
}

/// <summary>Attempts to get a write classified as a read (each must need confirmation at the "except reads" levels).</summary>
public class SafeModePolicyBypassTests
{
    [Theory]
    // "key" is an unreserved word: a table called key right before a data-modifying UPDATE is not FOR NO KEY UPDATE.
    [InlineData("WITH x AS (SELECT * FROM key) UPDATE t SET a = 1")]
    [InlineData("WITH x AS (SELECT 1 AS key) UPDATE t SET a = 1")]
    [InlineData("select 1; drop table x")]
    [InlineData("SELECT 1 /* ; */ ; DELETE FROM t")]
    [InlineData("select 'a\\'; drop table x; --'")]
    [InlineData("SELECT E'\\\\'; DELETE FROM t; --'")]
    [InlineData("select$$x$$; delete from t")]
    [InlineData("/* hi */ DeLeTe FROM t")]
    [InlineData("-- select\nDELETE FROM t")]
    [InlineData("EXPLAIN (ANALYZE) DELETE FROM t")]
    [InlineData("EXPLAIN (FORMAT JSON, ANALYZE TRUE) DELETE FROM t")]
    [InlineData("EXPLAIN ANALYZE WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("EXPLAIN ANALYZE SELECT * INTO t2 FROM t")]
    [InlineData("EXPLAIN ANALYZE MERGE INTO t USING s ON true WHEN MATCHED THEN DELETE")]
    [InlineData("SELECT * INTO TEMP t2 FROM t")]
    [InlineData("TABLE t; TRUNCATE t")]
    [InlineData("VALUES (1); DELETE FROM t")]
    [InlineData("SHOW x; SET y = 1")]
    [InlineData("select * from t where a = 'it''s'; delete from t")]
    [InlineData("WITH a AS (SELECT 1 FOR UPDATE) UPDATE t SET a = 1")]
    [InlineData("SELECT 1; DELETE FROM t")]
    public void Writes_are_never_read_only(string sql) => Assert.False(SafeModePolicy.AllReadOnly([sql]));

    [Theory]
    [InlineData("SELECT * FROM t FOR NO KEY UPDATE")]
    [InlineData("SELECT * FROM t FOR UPDATE OF t SKIP LOCKED")]
    [InlineData("SELECT \"a\"\";delete from t;--\" FROM t")]
    [InlineData("/* /* nested */ DELETE FROM t */ SELECT 1")]
    public void Reads_stay_read_only(string sql) => Assert.True(SafeModePolicy.AllReadOnly([sql]));
}
