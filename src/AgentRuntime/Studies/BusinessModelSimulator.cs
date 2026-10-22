using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentRuntime.Studies;

/// <summary>An input of the business model: a range (low, likely, high) and where it came from.</summary>
public sealed record Driver(string Name, double Low, double Likely, double High, string Source, string? AssumptionId = null)
{
    /// <summary>Taken from an assumption in the study's register.</summary>
    public const string FromAssumption = "assumption";
    /// <summary>A number given directly in the projection (no evidence attached).</summary>
    public const string Given = "given";
    /// <summary>Not given: the model's default.</summary>
    public const string Default = "default";

    public bool Uncertain => High - Low > 1e-12;
}

/// <summary>What the business model knows about each of its inputs.</summary>
public sealed record DriverInfo(string Name, string Description, double Min, double Max, double? Default, string Unit, bool Whole = false);

/// <summary>A projection to run: the business model's drivers and its settings.</summary>
public sealed record ProjectionSpec
{
    public required string Name { get; init; }
    /// <summary>"subscription" (customers pay every month until they churn), "one_time" (each
    /// customer buys once) or "usage" (active customers buy purchases_per_month times a month).</summary>
    public string RevenueModel { get; init; } = "subscription";
    public int HorizonMonths { get; init; } = 36;
    public int Iterations { get; init; } = 2000;
    public int Seed { get; init; } = 7;
    public string Currency { get; init; } = "USD";
    public required IReadOnlyDictionary<string, Driver> Drivers { get; init; }
}

/// <summary>
/// The business model behind an idea study's projections (docs/studies.md, "Evaluating an idea").
/// The runtime computes it, not the LLM: agents choose the drivers (from assumptions that cite
/// evidence), and this simulates the months ahead many times, each time drawing every uncertain
/// driver from its range (a triangular distribution over low, likely, high). It reports the spread
/// of revenue, profit and cash, how likely the idea is to pay back, its value today (NPV), the
/// funding it needs, unit economics, and which drivers move the result most.
/// </summary>
public static class BusinessModelSimulator
{
    public const int MaxHorizonMonths = 120;
    public const int MaxIterations = 10_000;
    public static readonly string[] RevenueModels = ["subscription", "one_time", "usage"];

    public static readonly IReadOnlyList<DriverInfo> Catalog =
    [
        new("market_size", "Potential customers you could reach", 1, 1e10, null, "customers", Whole: true),
        new("adoption_rate", "Share of that market that would ever buy at this price (e.g. from a customer experiment)", 0, 1, 1, "share"),
        new("initial_customers", "Paying customers at launch (pilots, waitlist, existing clients)", 0, 1e9, 0, "customers", Whole: true),
        new("price", "Revenue per purchase (per month, for a subscription)", 0, 1e9, null, "currency"),
        new("purchases_per_month", "Purchases per active customer per month (1 for a subscription)", 0, 1000, 1, "purchases"),
        new("monthly_churn", "Share of active customers lost each month (one_time: everyone, after their purchase)", 0, 1, 0.03, "share"),
        new("marketing_spend", "Paid acquisition spend per month, from launch", 0, 1e9, 0, "currency"),
        new("cac", "Cost to acquire one paying customer through paid channels", 0.01, 1e9, null, "currency"),
        new("organic_rate", "New customers each month per active customer (word of mouth, referrals)", 0, 1, 0, "share"),
        new("variable_cost", "Cost of each purchase (hosting, materials, payment fees, support)", 0, 1e9, 0, "currency"),
        new("fixed_costs", "Fixed costs per month (salaries, rent, tools), from month 1", 0, 1e9, 0, "currency"),
        new("upfront_investment", "One-time cost before launch (building it, equipment)", 0, 1e12, 0, "currency"),
        new("launch_month", "Months of building before the first sale", 0, 60, 0, "months", Whole: true),
        new("discount_rate", "Annual rate future cash is discounted at, for NPV", 0, 1, 0.12, "share"),
    ];

    private static readonly Dictionary<string, DriverInfo> ByName = Catalog.ToDictionary(d => d.Name);

    public static DriverInfo? Info(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// Reads the drivers of a projection. Each is a number, an assumption id ("asm-…" or
    /// {"assumption": "asm-…"}), or a range {"low", "likely", "high"}. Drivers not given take their
    /// default; market_size and price have none, and cac is needed when marketing_spend is.
    /// </summary>
    public static (Dictionary<string, Driver>? Drivers, List<string> Errors) ParseDrivers(
        JsonObject? given, IReadOnlyDictionary<string, StudyAssumption> assumptions, string revenueModel)
    {
        var errors = new List<string>();
        var drivers = new Dictionary<string, Driver>();
        foreach (var (name, node) in given ?? [])
        {
            if (Info(name) is not { } info)
            {
                errors.Add($"Unknown driver '{name}'. Drivers: {string.Join(", ", Catalog.Select(d => d.Name))}.");
                continue;
            }

            var driver = ParseOne(info, node, assumptions, errors);
            if (driver is not null) drivers[name] = driver;
        }

        foreach (var info in Catalog.Where(i => !drivers.ContainsKey(i.Name) && i.Default is not null))
        {
            drivers[info.Name] = new Driver(info.Name, info.Default!.Value, info.Default.Value, info.Default.Value, Driver.Default);
        }

        if (revenueModel == "one_time") drivers["monthly_churn"] = new Driver("monthly_churn", 1, 1, 1, Driver.Default);
        if (revenueModel == "subscription" && drivers.TryGetValue("purchases_per_month", out var p) && p.Source == Driver.Default)
        {
            drivers["purchases_per_month"] = p with { Low = 1, Likely = 1, High = 1 };
        }

        foreach (var required in new[] { "market_size", "price" }.Where(r => !drivers.ContainsKey(r) && !errors.Any(e => e.Contains($"'{r}'"))))
        {
            errors.Add($"{required} is required ({Info(required)!.Description.ToLowerInvariant()}).");
        }

        if (drivers.TryGetValue("marketing_spend", out var spend) && spend.High > 0 && !drivers.ContainsKey("cac"))
        {
            errors.Add("marketing_spend needs cac: what one paying customer costs to acquire.");
        }

        return (errors.Count == 0 ? drivers : null, errors);
    }

    private static Driver? ParseOne(DriverInfo info, JsonNode? node, IReadOnlyDictionary<string, StudyAssumption> assumptions, List<string> errors)
    {
        double low, likely, high;
        var source = Driver.Given;
        var assumptionId = node switch
        {
            JsonValue v when v.TryGetValue<string>(out var text) && text.Trim().StartsWith("asm-", StringComparison.Ordinal) => text.Trim(),
            JsonObject o when o["assumption"] is JsonValue a && a.TryGetValue<string>(out var id) => id.Trim(),
            _ => null
        };
        if (assumptionId is not null)
        {
            if (!assumptions.TryGetValue(assumptionId, out var assumption))
            {
                errors.Add($"{info.Name}: no assumption '{assumptionId}' in this study.");
                return null;
            }

            (low, likely, high) = (assumption.Low, assumption.Likely, assumption.High);
            source = Driver.FromAssumption;
        }
        else if (Number(node) is { } number)
        {
            low = likely = high = number;
        }
        else if (node is JsonObject o && Number(o["likely"]) is { } l)
        {
            (low, likely, high) = (Number(o["low"]) ?? l, l, Number(o["high"]) ?? l);
        }
        else
        {
            errors.Add($"{info.Name}: give a number, an assumption id, or {{\"low\", \"likely\", \"high\"}}.");
            return null;
        }

        if (!(low <= likely && likely <= high))
        {
            errors.Add($"{info.Name}: low ≤ likely ≤ high (got {low}, {likely}, {high}).");
            return null;
        }

        if (low < info.Min || high > info.Max)
        {
            errors.Add(info.Unit == "share"
                ? $"{info.Name} is a share between 0 and 1: give 0.05 for 5% (got {low}–{high})."
                : $"{info.Name} must be between {info.Min} and {info.Max} (got {low}–{high}).");
            return null;
        }

        return new Driver(info.Name, low, likely, high, source, assumptionId);
    }

    /// <summary>A JSON number (of any CLR type behind the node) or a numeric string, as a double.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        var text = v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => v.ToJsonString(),
            System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            _ => null
        };
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>One simulated future: the drivers drawn, month by month.</summary>
    private sealed class Path(int months)
    {
        public readonly double[] Revenue = new double[months];
        public readonly double[] Profit = new double[months];
        public readonly double[] Cash = new double[months];
        public readonly double[] Customers = new double[months];
        public double Npv;
        public double EverAcquired;
        public double Cap;
        public double Upfront;
    }

    private static Path Simulate(IReadOnlyDictionary<string, double> d, int months)
    {
        var path = new Path(months);
        var churn = d["monthly_churn"];
        var launch = (int)Math.Round(d["launch_month"]);
        var cap = d["market_size"] * d["adoption_rate"];
        var monthlyRate = Math.Pow(1 + d["discount_rate"], 1.0 / 12) - 1;
        var cash = -d["upfront_investment"];
        var npv = -d["upfront_investment"];
        double active = 0, ever = 0;
        for (var m = 0; m < months; m++)
        {
            double revenue = 0, variable = 0, marketing = 0;
            if (m >= launch)
            {
                // Last month's customers bring referrals, then some of them leave; paid marketing
                // adds spend / cac; nobody beyond the market cap (market_size × adoption_rate) buys.
                var organic = active * d["organic_rate"];
                active *= 1 - churn;
                var paid = d.GetValueOrDefault("cac") is > 0 and var cac ? d["marketing_spend"] / cac : 0;
                var starting = m == launch ? d["initial_customers"] : 0;
                var added = Math.Max(0, Math.Min(starting + paid + organic, cap - ever));
                marketing = d["marketing_spend"];
                active += added;
                ever += added;
                var purchases = active * d["purchases_per_month"];
                revenue = purchases * d["price"];
                variable = purchases * d["variable_cost"];
            }

            var profit = revenue - variable - marketing - d["fixed_costs"];
            cash += profit;
            npv += profit / Math.Pow(1 + monthlyRate, m + 1);
            path.Revenue[m] = revenue;
            path.Profit[m] = profit;
            path.Cash[m] = cash;
            path.Customers[m] = active;
        }

        path.Npv = npv;
        path.EverAcquired = ever;
        path.Cap = cap;
        path.Upfront = d["upfront_investment"];
        return path;
    }

    private static double Draw(Driver driver, Random random)
    {
        if (!driver.Uncertain) return driver.Likely;
        // Triangular over (low, likely, high).
        double a = driver.Low, b = driver.High, c = driver.Likely, u = random.NextDouble();
        var f = (c - a) / (b - a);
        return u < f ? a + Math.Sqrt(u * (b - a) * (c - a)) : b - Math.Sqrt((1 - u) * (b - a) * (b - c));
    }

    private static Dictionary<string, double> Likely(IReadOnlyDictionary<string, Driver> drivers) =>
        drivers.ToDictionary(kv => kv.Key, kv => kv.Value.Likely);

    public static JsonObject Run(ProjectionSpec spec)
    {
        var months = Math.Clamp(spec.HorizonMonths, 1, MaxHorizonMonths);
        var iterations = Math.Clamp(spec.Iterations, 100, MaxIterations);
        var random = new Random(spec.Seed);
        var drivers = spec.Drivers;
        var paths = new Path[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var drawn = drivers.ToDictionary(kv => kv.Key, kv => Draw(kv.Value, random));
            paths[i] = Simulate(drawn, months);
        }

        var finalCash = paths.Select(p => p.Cash[^1]).ToArray();
        var npv = paths.Select(p => p.Npv).ToArray();
        // Funding need: how deep cash goes (the upfront investment included) before it recovers.
        var funding = paths.Select(p => Math.Max(0, -Math.Min(p.Cash.Min(), -p.Upfront))).ToArray();
        var payback = paths.Select(PaybackMonth).ToArray();
        var firstProfit = paths.Select(p => FirstProfitableMonth(p.Profit)).ToArray();
        var paid = payback.Where(m => m is not null).Select(m => (double)m!.Value).ToArray();
        var profitable = firstProfit.Where(m => m is not null).Select(m => (double)m!.Value).ToArray();

        var summary = new JsonObject
        {
            ["p_payback"] = Round(Share(finalCash, x => x >= 0), 4),
            ["p_profitable_at_end"] = Round(Share(paths.Select(p => p.Profit[^1]).ToArray(), x => x > 0), 4),
            ["p_npv_positive"] = Round(Share(npv, x => x > 0), 4),
            ["npv"] = Bands(npv),
            ["final_cash"] = Bands(finalCash),
            ["funding_need"] = Bands(funding),
            ["revenue_total"] = Bands(paths.Select(p => p.Revenue.Sum()).ToArray()),
            ["customers_at_end"] = Bands(paths.Select(p => p.Customers[^1]).ToArray(), 0),
            ["payback_month"] = new JsonObject
            {
                ["p50"] = paid.Length == 0 ? null : Round(Percentile(paid, 0.5), 0),
                ["p10"] = paid.Length == 0 ? null : Round(Percentile(paid, 0.1), 0),
                ["p90"] = paid.Length == 0 ? null : Round(Percentile(paid, 0.9), 0),
                ["never_share"] = Round(1 - (double)paid.Length / iterations, 4)
            },
            ["first_profitable_month"] = new JsonObject
            {
                ["p50"] = profitable.Length == 0 ? null : Round(Percentile(profitable, 0.5), 0),
                ["never_share"] = Round(1 - (double)profitable.Length / iterations, 4)
            }
        };

        var years = new JsonArray();
        for (var y = 0; y * 12 < months; y++)
        {
            var from = y * 12;
            var to = Math.Min(months, from + 12);
            years.Add(new JsonObject
            {
                ["year"] = y + 1,
                ["revenue"] = Bands(paths.Select(p => p.Revenue[from..to].Sum()).ToArray()),
                ["profit"] = Bands(paths.Select(p => p.Profit[from..to].Sum()).ToArray())
            });
        }

        var monthly = new JsonObject
        {
            ["revenue"] = Series(paths, p => p.Revenue, months),
            ["profit"] = Series(paths, p => p.Profit, months),
            ["cash"] = Series(paths, p => p.Cash, months),
            ["customers"] = Series(paths, p => p.Customers, months, 0)
        };

        return new JsonObject
        {
            ["name"] = spec.Name,
            ["revenue_model"] = spec.RevenueModel,
            ["currency"] = spec.Currency,
            ["horizon_months"] = months,
            ["iterations"] = iterations,
            ["seed"] = spec.Seed,
            ["summary"] = summary,
            ["unit_economics"] = UnitEconomics(drivers),
            ["years"] = years,
            ["monthly"] = monthly,
            ["sensitivity"] = Sensitivity(drivers, months),
            ["warnings"] = new JsonArray(Warnings(drivers, paths).Select(w => (JsonNode)w!).ToArray())
        };
    }

    /// <summary>The month (1-based) from which cash stays at or above zero; null if it ends below.</summary>
    private static int? PaybackMonth(Path p)
    {
        if (p.Cash[^1] < 0) return null;
        for (var m = p.Cash.Length - 1; m >= 0; m--)
        {
            if (p.Cash[m] < 0) return m + 2;
        }

        return 1;
    }

    private static int? FirstProfitableMonth(double[] profit)
    {
        for (var m = 0; m < profit.Length; m++)
        {
            if (profit[m] > 0) return m + 1;
        }

        return null;
    }

    private static JsonObject UnitEconomics(IReadOnlyDictionary<string, Driver> drivers)
    {
        var d = Likely(drivers);
        var margin = d["price"] - d["variable_cost"];
        var contribution = margin * d["purchases_per_month"];
        // A customer's lifetime: 1 / churn months, capped at 10 years when nobody leaves.
        var lifetime = d["monthly_churn"] > 0 ? Math.Min(1 / d["monthly_churn"], 120) : 120;
        var ltv = contribution * lifetime;
        var cac = d.GetValueOrDefault("cac");
        return new JsonObject
        {
            ["gross_margin"] = d["price"] > 0 ? Round(margin / d["price"], 4) : null,
            ["contribution_per_customer_month"] = Round(contribution, 2),
            ["customer_lifetime_months"] = Round(lifetime, 1),
            ["ltv"] = Round(ltv, 2),
            ["cac"] = cac > 0 ? Round(cac, 2) : null,
            ["ltv_to_cac"] = cac > 0 ? Round(ltv / cac, 2) : null,
            ["cac_payback_months"] = cac > 0 && contribution > 0 ? Round(cac / contribution, 1) : null,
            ["break_even_customers"] = contribution > 0 ? Round(d["fixed_costs"] / contribution, 0) : null
        };
    }

    /// <summary>For each uncertain driver: NPV with that driver at its low and at its high, every
    /// other at its likely value. Sorted by how far it moves NPV (a tornado chart).</summary>
    private static JsonObject Sensitivity(IReadOnlyDictionary<string, Driver> drivers, int months)
    {
        var baseline = Likely(drivers);
        var rows = new List<(string Name, double Low, double High, double AtLow, double AtHigh)>();
        foreach (var driver in drivers.Values.Where(d => d.Uncertain))
        {
            var at = new Dictionary<string, double>(baseline) { [driver.Name] = driver.Low };
            var atLow = Simulate(at, months).Npv;
            at[driver.Name] = driver.High;
            var atHigh = Simulate(at, months).Npv;
            rows.Add((driver.Name, driver.Low, driver.High, atLow, atHigh));
        }

        return new JsonObject
        {
            ["baseline_npv"] = Round(Simulate(baseline, months).Npv, 2),
            ["drivers"] = new JsonArray(rows.OrderByDescending(r => Math.Abs(r.AtHigh - r.AtLow)).Select(r => (JsonNode)new JsonObject
            {
                ["driver"] = r.Name,
                ["low"] = r.Low,
                ["high"] = r.High,
                ["npv_at_low"] = Round(r.AtLow, 2),
                ["npv_at_high"] = Round(r.AtHigh, 2),
                ["swing"] = Round(Math.Abs(r.AtHigh - r.AtLow), 2)
            }).ToArray())
        };
    }

    private static IEnumerable<string> Warnings(IReadOnlyDictionary<string, Driver> drivers, Path[] paths)
    {
        var d = Likely(drivers);
        if (d["variable_cost"] >= d["price"] && d["price"] > 0) yield return "Each purchase costs at least what it earns: the likely gross margin is zero or negative.";
        var contribution = (d["price"] - d["variable_cost"]) * d["purchases_per_month"];
        var lifetime = d["monthly_churn"] > 0 ? Math.Min(1 / d["monthly_churn"], 120) : 120;
        if (d.GetValueOrDefault("cac") is > 0 and var cac && contribution * lifetime < cac)
        {
            yield return "A customer brings in less than they cost to acquire (LTV below CAC) at the likely values.";
        }

        var saturated = paths.Count(p => p.Cap > 0 && p.EverAcquired >= 0.95 * p.Cap);
        if (saturated > paths.Length / 2) yield return "Growth hits the market cap (market_size × adoption_rate) in most simulated futures: the market, not marketing, limits revenue.";
        if (drivers.Values.All(v => !v.Uncertain)) yield return "No driver has a range, so every simulated future is the same: give low and high values to see the uncertainty.";
        if (drivers.Values.Any(v => v.Source == Driver.Given)) yield return $"Some drivers are plain numbers without evidence: {string.Join(", ", drivers.Values.Where(v => v.Source == Driver.Given).Select(v => v.Name))}.";
    }

    private static JsonObject Series(Path[] paths, Func<Path, double[]> pick, int months, int digits = 2)
    {
        var p10 = new JsonArray();
        var p50 = new JsonArray();
        var p90 = new JsonArray();
        var column = new double[paths.Length];
        for (var m = 0; m < months; m++)
        {
            for (var i = 0; i < paths.Length; i++) column[i] = pick(paths[i])[m];
            Array.Sort(column);
            p10.Add(Round(PercentileSorted(column, 0.1), digits));
            p50.Add(Round(PercentileSorted(column, 0.5), digits));
            p90.Add(Round(PercentileSorted(column, 0.9), digits));
        }

        return new JsonObject { ["p10"] = p10, ["p50"] = p50, ["p90"] = p90 };
    }

    private static JsonObject Bands(double[] values, int digits = 2) => new()
    {
        ["p10"] = Round(Percentile(values, 0.1), digits),
        ["p50"] = Round(Percentile(values, 0.5), digits),
        ["p90"] = Round(Percentile(values, 0.9), digits),
        ["mean"] = Round(values.Average(), digits)
    };

    private static double Share(double[] values, Func<double, bool> test) => values.Length == 0 ? 0 : (double)values.Count(test) / values.Length;

    private static double Percentile(double[] values, double q)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return PercentileSorted(sorted, q);
    }

    private static double PercentileSorted(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        var position = (sorted.Length - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
