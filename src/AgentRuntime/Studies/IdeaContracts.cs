using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Studies;

/// <summary>What a study is for (docs/studies.md): answering a question with data, or evaluating an
/// idea (a product, a business, a change) for feasibility, profitability and usefulness.</summary>
public static class StudyKinds
{
    public const string Question = "question";
    public const string Idea = "idea";

    public static string Normalize(string? kind) => kind?.Trim().ToLowerInvariant() == Idea ? Idea : Question;
}

/// <summary>
/// The idea an idea study evaluates, as the person described it. Free text on purpose: agents turn
/// it into assumptions (with ranges and evidence) rather than trusting the numbers in it.
/// </summary>
public sealed record IdeaBrief
{
    /// <summary>What the idea is.</summary>
    public string Idea { get; init; } = string.Empty;
    /// <summary>Who it's for.</summary>
    public string? Customers { get; init; }
    /// <summary>The problem it solves, and what people do about it today.</summary>
    public string? Problem { get; init; }
    /// <summary>How it makes money: prices, plans, who pays.</summary>
    public string? RevenueModel { get; init; }
    /// <summary>Known costs, team, budget, constraints.</summary>
    public string? Costs { get; init; }
    /// <summary>How far ahead projections look.</summary>
    public int HorizonMonths { get; init; } = 36;
    public string Currency { get; init; } = "USD";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static IdeaBrief? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<IdeaBrief>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Checks and trims a brief people sent; returns the error to show, if any.</summary>
    public static (IdeaBrief? Brief, string? Error) Clean(IdeaBrief? brief)
    {
        if (brief is null || string.IsNullOrWhiteSpace(brief.Idea)) return (null, "Describe the idea.");
        static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
        var currency = (brief.Currency ?? "USD").Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) return (null, "Currency is a three-letter code, like USD or EUR.");
        if (brief.HorizonMonths is < 6 or > BusinessModelSimulator.MaxHorizonMonths)
        {
            return (null, $"The horizon is between 6 and {BusinessModelSimulator.MaxHorizonMonths} months.");
        }

        return (new IdeaBrief
        {
            Idea = Trim(brief.Idea, 4000)!,
            Customers = Trim(brief.Customers, 2000),
            Problem = Trim(brief.Problem, 2000),
            RevenueModel = Trim(brief.RevenueModel, 2000),
            Costs = Trim(brief.Costs, 2000),
            HorizonMonths = brief.HorizonMonths,
            Currency = currency
        }, null);
    }

    /// <summary>The brief as the run's agents read it.</summary>
    public string Render()
    {
        var lines = new List<string> { $"The idea: {Idea}" };
        if (Customers is not null) lines.Add($"Who it's for: {Customers}");
        if (Problem is not null) lines.Add($"The problem it solves (and today's alternative): {Problem}");
        if (RevenueModel is not null) lines.Add($"How it makes money: {RevenueModel}");
        if (Costs is not null) lines.Add($"Known costs and constraints: {Costs}");
        lines.Add($"Horizon: {HorizonMonths} months. Currency: {Currency}.");
        return string.Join("\n", lines);
    }
}

/// <summary>Categories of assumptions, so the register reads by theme.</summary>
public static class AssumptionCategories
{
    public static readonly string[] All = ["market", "customer", "pricing", "cost", "operations", "technical", "regulatory", "competition", "other"];

    public static string Normalize(string? category) =>
        All.FirstOrDefault(c => string.Equals(c, category?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "other";
}

/// <summary>
/// One number an idea's case rests on (market size, price, churn, cost to acquire a customer...), as
/// a range with the evidence behind it. Projections read their inputs from assumptions, so every
/// number in a projection traces back to where it came from. Recording an assumption with the same
/// name again supersedes the old one.
/// </summary>
public sealed record StudyAssumption
{
    public string AssumptionId { get; init; } = StudyIds.Short("asm");
    public required string StudyId { get; init; }
    public string? RunId { get; init; }
    public required string AgentId { get; init; }
    /// <summary>Lowercase with underscores, e.g. "monthly_churn".</summary>
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = "other";
    public string? Unit { get; init; }
    public required double Low { get; init; }
    public required double Likely { get; init; }
    public required double High { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Rationale { get; init; }
    /// <summary>"candidate", "accepted" or "challenged".</summary>
    public string Status { get; init; } = "candidate";
    public string? ReviewerAgentId { get; init; }
    public string? ReviewNotes { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    /// <summary>False once an assumption with the same name was recorded after it.</summary>
    public bool Current { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Supported => Evidence.Count > 0;
}

/// <summary>A financial projection: the business model simulated many times over the horizon.</summary>
public sealed record StudyProjection
{
    public string ProjectionId { get; init; } = StudyIds.Short("prj");
    public required string StudyId { get; init; }
    public string? RunId { get; init; }
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    /// <summary>The drivers as resolved (ranges and where each came from) and the settings.</summary>
    public string SpecJson { get; init; } = "{}";
    /// <summary>What the simulation produced (<see cref="BusinessModelSimulator"/>).</summary>
    public string ResultJson { get; init; } = "{}";
    public required string EvidenceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
