using System.Text.Json;
using AgentRuntime.Workspaces;
using Xunit;

namespace AgentRuntime.Tests;

public class WatchEvaluatorTests
{
    // Shopify-style: an http-api result whose body is a JSON *string*.
    private static readonly string ShopResult = JsonSerializer.Serialize(new
    {
        status = 200,
        body = JsonSerializer.Serialize(new
        {
            products = new object[]
            {
                new { title = "Mug", variants = new object[] { new { sku = "MUG-S", inventory_quantity = 3 }, new { sku = "MUG-L", inventory_quantity = 40 } } },
                new { title = "Tee", variants = new object[] { new { sku = "TEE-M", inventory_quantity = 9 } } },
                new { title = "Cap", variants = new object[] { new { sku = "CAP-1", inventory_quantity = 10 } } }
            }
        })
    });

    private static WatchRule LowStock(string op = "<", string value = "10") => new()
    {
        ItemsPath = "$.body.products[*].variants[*]",
        Conditions = [new WatchCondition { Field = "inventory_quantity", Op = op, Value = value }],
        KeyField = "sku",
        DisplayFields = ["sku", "inventory_quantity"]
    };

    [Fact]
    public void FindsMatchingItems_ThroughJsonEncodedStrings()
    {
        var eval = WatchEvaluator.Evaluate(ShopResult, LowStock());

        Assert.Equal(4, eval.ItemCount);
        Assert.Equal(["MUG-S", "TEE-M"], eval.Matches.Select(m => m.Key));
        Assert.Equal("sku=MUG-S, inventory_quantity=3", eval.Matches[0].Summary);
    }

    [Theory]
    [InlineData("<=", "10", 3)]
    [InlineData(">", "9", 2)]
    [InlineData("==", "10", 1)]
    [InlineData("!=", "10", 3)]
    public void NumericComparisons(string op, string value, int expected) =>
        Assert.Equal(expected, WatchEvaluator.Evaluate(ShopResult, LowStock(op, value)).Matches.Count);

    [Fact]
    public void AllConditionsMustHold_AndStringOpsIgnoreCase()
    {
        var json = """{"orders":[{"id":1,"status":"FAILED","tags":["vip"]},{"id":2,"status":"paid","tags":[]},{"id":3,"status":"failed","tags":["new"]}]}""";
        var rule = new WatchRule
        {
            ItemsPath = "$.orders[*]",
            KeyField = "id",
            Conditions =
            [
                new WatchCondition { Field = "status", Op = "==", Value = "failed" },
                new WatchCondition { Field = "tags", Op = "contains", Value = "VIP" }
            ]
        };

        Assert.Equal(["1"], WatchEvaluator.Evaluate(json, rule).Matches.Select(m => m.Key));
    }

    [Fact]
    public void ExistsAndIndexes()
    {
        var json = """{"items":[{"a":{"b":[5,6]}},{"a":{}}]}""";
        var exists = new WatchRule { ItemsPath = "$.items[*]", Conditions = [new() { Field = "a.b[1]", Op = "exists" }] };
        var missing = new WatchRule { ItemsPath = "$.items[*]", Conditions = [new() { Field = "a.b", Op = "not_exists" }] };
        var last = new WatchRule { ItemsPath = "$['items'][0].a.b[-1]", Conditions = [new() { Field = "$", Op = "==", Value = "6" }] };

        Assert.Single(WatchEvaluator.Evaluate(json, exists).Matches);
        Assert.Single(WatchEvaluator.Evaluate(json, missing).Matches);
        Assert.Single(WatchEvaluator.Evaluate(json, last).Matches);
    }

    [Fact]
    public void MissingKeyField_FallsBackToAStableContentHash()
    {
        var rule = LowStock() with { KeyField = null };
        var a = WatchEvaluator.Evaluate(ShopResult, rule).Matches.Select(m => m.Key).ToList();
        var b = WatchEvaluator.Evaluate(ShopResult, rule).Matches.Select(m => m.Key).ToList();
        Assert.Equal(a, b);
        Assert.Equal(2, a.Distinct().Count());
    }

    [Theory]
    [InlineData("inventory_quantity", "~", "1", "Unknown operator")]
    [InlineData("inventory_quantity", "<", null, "needs a value")]
    [InlineData("a[?(@.x)]", "exists", null, "unsupported selector")]
    public void InvalidRules_AreRejectedWithAReason(string field, string op, string? value, string expected)
    {
        var error = WatchEvaluator.Validate(new WatchRule { Conditions = [new() { Field = field, Op = op, Value = value }] });
        Assert.Contains(expected, error);
    }

    [Fact]
    public void NoConditions_IsRejected() => Assert.NotNull(WatchEvaluator.Validate(new WatchRule()));
}
