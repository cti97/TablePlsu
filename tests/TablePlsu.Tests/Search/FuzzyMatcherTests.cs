using TablePlsu.Core.Search;

namespace TablePlsu.Tests.Search;

public class FuzzyMatcherTests
{
    [Fact]
    public void Subsequence_matches_with_indices()
    {
        var m = FuzzyMatcher.Match("ord", "sales.orders");
        Assert.NotNull(m);
        Assert.Equal(new[] { 6, 7, 8 }, m!.Value.Indices);
    }

    [Fact]
    public void Non_subsequence_does_not_match() => Assert.Null(FuzzyMatcher.Match("xyz", "orders"));

    [Fact]
    public void Case_insensitive() => Assert.NotNull(FuzzyMatcher.Match("ORD", "orders"));

    [Fact]
    public void Empty_pattern_matches_everything_with_zero_indices()
    {
        var m = FuzzyMatcher.Match("", "orders");
        Assert.NotNull(m);
        Assert.Empty(m!.Value.Indices);
    }

    [Fact]
    public void Consecutive_and_word_start_matches_score_higher()
    {
        var exactish = FuzzyMatcher.Match("ord", "orders")!.Value.Score;
        var scattered = FuzzyMatcher.Match("ord", "o_r_d_x")!.Value.Score;
        Assert.True(exactish > scattered);

        var wordStart = FuzzyMatcher.Match("oi", "order_items")!.Value.Score;
        var mid = FuzzyMatcher.Match("oi", "potion")!.Value.Score;
        Assert.True(wordStart > mid);
    }

    [Fact]
    public void Exact_match_beats_longer_candidate()
    {
        Assert.True(FuzzyMatcher.Match("orders", "orders")!.Value.Score > FuzzyMatcher.Match("orders", "orders_archive")!.Value.Score);
    }

    [Fact]
    public void Prefers_word_boundary_occurrence_over_first_occurrence()
    {
        // Greedy-first would pick the 'i' in "invoice"; the boundary match is "items".
        var m = FuzzyMatcher.Match("it", "invoice_items")!.Value;
        Assert.Equal(new[] { 8, 9 }, m.Indices);
    }

    [Theory]
    [InlineData("^ord", "orders", true)]
    [InlineData("^ord", "sales_orders", false)]
    [InlineData("_items$", "order_items", true)]
    [InlineData("_items$", "order_items_x", false)]
    [InlineData("^orders$", "orders", true)]
    [InlineData("^orders$", "orders2", false)]
    [InlineData("^ORD", "orders", true)]
    public void Anchors(string pattern, string candidate, bool matches) =>
        Assert.Equal(matches, FuzzyMatcher.Match(pattern, candidate) is not null);

    [Fact]
    public void Anchor_indices_are_contiguous()
    {
        Assert.Equal(new[] { 0, 1, 2 }, FuzzyMatcher.Match("^ord", "orders")!.Value.Indices);
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10 }, FuzzyMatcher.Match("_items$", "order_items")!.Value.Indices);
    }

    [Fact]
    public void Filter_sorts_by_score_and_limits()
    {
        var items = new[] { "o_r_d", "orders", "customers", "order_items" };
        var result = FuzzyMatcher.Filter(items, "ord", s => s, limit: 2);
        Assert.Equal(2, result.Count);
        Assert.Equal("orders", result[0].Item);
        Assert.DoesNotContain(result, r => r.Item == "customers");
    }

    [Fact]
    public void Contains_strategy()
    {
        Assert.NotNull(FuzzyMatcher.MatchContains("der", "orders"));
        Assert.Null(FuzzyMatcher.MatchContains("odr", "orders"));
        Assert.Equal(new[] { 2, 3, 4 }, FuzzyMatcher.MatchContains("DER", "orders")!.Value.Indices);
    }

    [Fact]
    public void IsMatch_agrees_with_Match_and_MatchContains()
    {
        string[] candidates = ["orders", "sales.orders", "customers", "active_customers", "Customer_9", "a", "", "OrderTotals", "xyz_ord", "ordo"];
        string[] patterns = ["", "o", "ord", "ORD", "ordr", "cust", "customers", "customerss", "^cust", "ers$", "^orders$", "^", "$", "^$", "s.o", "zz", "a", "ordo", "oo"];
        foreach (var p in patterns)
        foreach (var c in candidates)
        {
            Assert.True((FuzzyMatcher.Match(p, c) is not null) == FuzzyMatcher.IsMatch(p, c), $"fuzzy '{p}' vs '{c}'");
            Assert.True((FuzzyMatcher.MatchContains(p, c) is not null) == FuzzyMatcher.IsMatch(p, c, contains: true), $"contains '{p}' vs '{c}'");
        }
    }
}
