using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.Contracts;
using AgentRuntime.Events;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Studies;

/// <summary>An experiment as an agent describes it (the arguments of run_simulation).</summary>
public sealed record ExperimentSpec
{
    public required string Name { get; init; }
    public required int Size { get; init; }
    public required IReadOnlyList<SegmentSpec> Segments { get; init; }
    public IReadOnlyList<string> PopulationEvidence { get; init; } = [];
    /// <summary>The first is the control.</summary>
    public required IReadOnlyList<(string Name, string Scenario)> Conditions { get; init; }
    public required string Question { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public (string Name, double Min, double Max)? Value { get; init; }
    public int Rounds { get; init; } = 1;
    public bool WordOfMouth { get; init; }
    public int Replications { get; init; } = 1;
    public IReadOnlyList<(string Fact, string? EvidenceId)> Facts { get; init; } = [];
    public (string Option, double Rate, string? EvidenceId)? Calibration { get; init; }
    public int Seed { get; init; } = 42;

    /// <summary>Every evidence id the spec cites (population, facts, calibration).</summary>
    public IEnumerable<string> CitedEvidence =>
        PopulationEvidence.Concat(Facts.Select(f => f.EvidenceId).OfType<string>())
            .Concat(Calibration?.EvidenceId is { } c ? [c] : []).Distinct();
}

public sealed record SegmentSpec(string Name, double Share, string Description,
    IReadOnlyDictionary<string, string> Attributes, IReadOnlyDictionary<string, (double Min, double Max)> Ranges);

public sealed record Participant(string Id, string Segment, string Description, IReadOnlyDictionary<string, string> Attributes);

/// <summary>What an experiment produced: one row per decision, and how the runtime sums it up.</summary>
public sealed record ExperimentOutcome
{
    public required List<Dictionary<string, object?>> Rows { get; init; }
    public required JsonObject Summary { get; init; }
    public int Participants { get; init; }
    public long Tokens { get; init; }
    public decimal CostUsd { get; init; }
    public int Calls { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// Runs a simulated experiment (docs/studies.md, "Experiments"): generates a population from the
/// segments (seeded, so it can be reproduced), and asks every participant, in every condition,
/// round and replication, for one structured decision with the organization's fast model. The
/// participants are not agents with tools: they can only decide, so the output is a table, not a
/// conversation. The runtime enforces the caps and the cost limit, whatever the spec asks for.
/// </summary>
public sealed class ExperimentRunner(ILLMProvider llm, ILlmSettingsResolver llmSettings, IEventPublisher events, IOptions<StudyOptions> options)
{
    public const string DecideTool = "decide";

    /// <summary>Parses run_simulation's arguments, clamped to the limits; errors are for the agent.</summary>
    public static (ExperimentSpec? Spec, List<string> Errors, List<string> Notes) Parse(JsonObject args, StudyOptions limits)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;
        double? Num(JsonNode? n) => n is JsonValue v && (v.TryGetValue<double>(out var d) || (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))) ? d : null;
        int Clamp(int? value, int min, int max, string what, int fallback)
        {
            var v = value ?? fallback;
            if (v > max) notes.Add($"{what} capped at {max}.");
            return Math.Clamp(v, min, max);
        }

        var name = Str(args["name"]);
        if (name.Length == 0) errors.Add("name is required.");

        var population = args["population"] as JsonObject;
        var segments = new List<SegmentSpec>();
        foreach (var node in population?["segments"] as JsonArray ?? [])
        {
            if (node is not JsonObject seg) continue;
            var attributes = (seg["attributes"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            var ranges = new Dictionary<string, (double, double)>();
            foreach (var (key, range) in seg["ranges"] as JsonObject ?? [])
            {
                if (range is JsonArray { Count: 2 } r && Num(r[0]) is { } lo && Num(r[1]) is { } hi && hi >= lo) ranges[key] = (lo, hi);
            }

            segments.Add(new SegmentSpec(Str(seg["name"]) is { Length: > 0 } n ? n : $"segment {segments.Count + 1}",
                Math.Max(0, Num(seg["share"]) ?? 0), Str(seg["description"]), attributes, ranges));
        }

        if (segments.Count == 0) errors.Add("population.segments needs at least one segment (name, share, description, attributes).");
        if (segments.Count > 0 && segments.Sum(s => s.Share) <= 0) errors.Add("Segment shares must add up to more than 0.");
        var populationEvidence = StudyReportValidator.Strings(population?["evidence"]);
        if (populationEvidence.Count == 0)
        {
            errors.Add("population.evidence must cite the evidence (from query_dataset, fit_model or documents) the segments " +
                       "and their shares come from: a simulated population has to be built from the study's data.");
        }

        var conditions = new List<(string, string)>();
        foreach (var node in args["conditions"] as JsonArray ?? [])
        {
            if (node is JsonObject c && Str(c["scenario"]) is { Length: > 0 } scenario)
            {
                conditions.Add((Str(c["name"]) is { Length: > 0 } n ? n : conditions.Count == 0 ? "control" : $"treatment {conditions.Count}", scenario));
            }
        }

        if (conditions.Count == 0) errors.Add("conditions needs at least a control: [{name, scenario}].");
        if (conditions.Count > limits.MaxConditions)
        {
            notes.Add($"Only the first {limits.MaxConditions} conditions are run.");
            conditions = conditions.Take(limits.MaxConditions).ToList();
        }

        var decision = args["decision"] as JsonObject;
        var question = Str(decision?["question"]);
        var choices = StudyReportValidator.Strings(decision?["options"]).Take(8).ToList();
        if (question.Length == 0 || choices.Count < 2) errors.Add("decision needs a question and at least two options.");
        (string, double, double)? value = null;
        if (decision?["value"] is JsonObject v && Str(v["name"]) is { Length: > 0 } valueName && Num(v["min"]) is { } min && Num(v["max"]) is { } max && max > min)
        {
            value = (valueName, min, max);
        }

        var facts = new List<(string, string?)>();
        foreach (var node in args["facts"] as JsonArray ?? [])
        {
            if (node is JsonObject f && Str(f["fact"]) is { Length: > 0 } fact) facts.Add((fact, Str(f["evidence_id"]) is { Length: > 0 } e ? e : null));
        }

        (string, double, string?)? calibration = null;
        if (args["calibration"] is JsonObject cal && Str(cal["option"]) is { Length: > 0 } calOption && Num(cal["rate"]) is { } rate)
        {
            if (!choices.Contains(calOption)) errors.Add($"calibration.option '{calOption}' isn't one of the decision's options.");
            calibration = (calOption, rate > 1 ? rate / 100 : rate, Str(cal["evidence_id"]) is { Length: > 0 } ce ? ce : null);
        }

        if (errors.Count > 0) return (null, errors, notes);
        return (new ExperimentSpec
        {
            Name = name,
            Size = Clamp((int?)Num(population?["size"]), 1, limits.MaxParticipants, "Population size", 20),
            Segments = segments,
            PopulationEvidence = populationEvidence,
            Conditions = conditions,
            Question = question,
            Options = choices,
            Value = value,
            Rounds = Clamp((int?)Num(args["rounds"]), 1, limits.MaxRounds, "Rounds", 1),
            WordOfMouth = args["word_of_mouth"] is JsonValue wom && wom.TryGetValue<bool>(out var w) && w,
            Replications = Clamp((int?)Num(args["replications"]), 1, limits.MaxReplications, "Replications", 1),
            Facts = facts,
            Calibration = calibration,
            Seed = (int?)Num(args["seed"]) ?? 42
        }, errors, notes);
    }

    /// <summary>The population: segment sizes by largest remainder of their shares, and each
    /// participant's numeric attributes drawn from its segment's ranges. Same seed, same people.</summary>
    public static List<Participant> GeneratePopulation(ExperimentSpec spec, int replication)
    {
        var total = spec.Segments.Sum(s => s.Share);
        var exact = spec.Segments.Select(s => s.Share / total * spec.Size).ToList();
        var counts = exact.Select(e => (int)Math.Floor(e)).ToList();
        foreach (var index in exact.Select((e, i) => (Remainder: e - Math.Floor(e), Index: i)).OrderByDescending(x => x.Remainder).Select(x => x.Index).Take(spec.Size - counts.Sum()))
        {
            counts[index]++;
        }

        var random = new Random(spec.Seed + replication * 7919);
        var people = new List<Participant>();
        for (var s = 0; s < spec.Segments.Count; s++)
        {
            var segment = spec.Segments[s];
            for (var k = 0; k < counts[s]; k++)
            {
                var attributes = new Dictionary<string, string>(segment.Attributes);
                foreach (var (key, (min, max)) in segment.Ranges)
                {
                    var drawn = min + random.NextDouble() * (max - min);
                    attributes[key] = (max - min >= 10 ? Math.Round(drawn) : Math.Round(drawn, 2)).ToString(CultureInfo.InvariantCulture);
                }

                people.Add(new Participant($"p{people.Count + 1:00}", segment.Name, segment.Description, attributes));
            }
        }

        return people;
    }

    public async Task<ExperimentOutcome> RunAsync(ExperimentSpec spec, string tenantId, string runId, string workspaceId, string agentId, CancellationToken ct)
    {
        var limits = options.Value;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var settings = await llmSettings.ResolveAsync(tenantId, cancellationToken: ct);
        var useFast = !string.IsNullOrWhiteSpace(settings.FastModel);
        var model = settings.ModelFor(useFast);
        var tool = DecisionTool(spec);
        var rows = new ConcurrentBag<Dictionary<string, object?>>();
        long tokens = 0;
        decimal cost = 0;
        var calls = 0;
        var unanswered = 0;
        var stoppedForCost = false;
        using var gate = new SemaphoreSlim(Math.Max(1, limits.SimulationConcurrency));
        var participantsSeen = 0;

        for (var rep = 1; rep <= spec.Replications && !stoppedForCost; rep++)
        {
            var people = GeneratePopulation(spec, rep);
            participantsSeen = Math.Max(participantsSeen, people.Count);
            foreach (var (conditionName, scenario) in spec.Conditions)
            {
                var previous = new Dictionary<string, (string Choice, double? Value, string Reason)>();
                for (var round = 1; round <= spec.Rounds && !stoppedForCost; round++)
                {
                    var random = new Random(spec.Seed * 31 + rep * 101 + round);
                    var heard = spec.WordOfMouth && round > 1
                        ? people.ToDictionary(p => p.Id, p => previous.Where(kv => kv.Key != p.Id).OrderBy(_ => random.Next()).Take(3)
                            .Select(kv => $"Someone else ({people.First(x => x.Id == kv.Key).Segment}) chose \"{kv.Value.Choice}\": {kv.Value.Reason}").ToList())
                        : people.ToDictionary(p => p.Id, _ => new List<string>());
                    var decided = new ConcurrentDictionary<string, (string, double?, string)>();
                    var roundCopy = round;
                    var repCopy = rep;
                    await Task.WhenAll(people.Select(async person =>
                    {
                        await gate.WaitAsync(ct);
                        try
                        {
                            if (stoppedForCost || cost >= limits.MaxSimulationCostUsd)
                            {
                                stoppedForCost = true;
                                return;
                            }

                            var messages = new List<ChatMessage>
                            {
                                ChatMessage.System(
                                    "You are role-playing one specific person in a research simulation. Decide as this person " +
                                    "realistically would given who they are, their situation and money, not as a helpful assistant: " +
                                    "people disagree, hesitate, and act on price, habit and what they hear. Respond only by calling " +
                                    $"the {DecideTool} tool, with a short first-person reason."),
                                ChatMessage.User(Prompt(spec, person, conditionName, scenario, roundCopy,
                                    previous.TryGetValue(person.Id, out var mine) ? mine : null, heard[person.Id]))
                            };
                            var response = await llm.CompleteAsync(new LlmCompletionRequest
                            {
                                Messages = messages,
                                Tools = [tool],
                                TenantId = tenantId,
                                Model = model,
                                Temperature = 0.9,
                                MaxTokens = 1024
                            }, ct);
                            var callCost = settings.CostOf(response, fast: useFast);
                            lock (rows)
                            {
                                tokens += response.InputTokens + response.OutputTokens;
                                cost += callCost;
                                calls++;
                            }

                            await events.PublishAsync(new RuntimeEvent
                            {
                                Type = RuntimeEventType.LlmCallCompleted,
                                AgentId = agentId,
                                TaskId = runId,
                                TenantId = tenantId,
                                Summary = $"Simulated participant {person.Id} decided ({spec.Name}).",
                                Data = new Dictionary<string, string>
                                {
                                    ["provider"] = settings.Provider,
                                    ["model"] = model,
                                    ["profile_id"] = settings.ProfileId ?? ModelProfiles.ServerId,
                                    ["profile_name"] = settings.ProfileName ?? "Server default",
                                    ["role"] = "Simulated participant",
                                    ["workspace_id"] = workspaceId,
                                    ["purpose"] = "simulation",
                                    ["input_tokens"] = response.InputTokens.ToString(CultureInfo.InvariantCulture),
                                    ["output_tokens"] = response.OutputTokens.ToString(CultureInfo.InvariantCulture),
                                    ["cached_input_tokens"] = response.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
                                    ["cost_usd"] = callCost.ToString(CultureInfo.InvariantCulture),
                                    ["duration_ms"] = "0"
                                }
                            }, ct);

                            var answer = ReadDecision(spec, response);
                            if (answer is null)
                            {
                                Interlocked.Increment(ref unanswered);
                                return;
                            }

                            decided[person.Id] = answer.Value;
                            var row = new Dictionary<string, object?>
                            {
                                ["replication"] = repCopy,
                                ["condition"] = conditionName,
                                ["round"] = roundCopy,
                                ["participant"] = person.Id,
                                ["segment"] = person.Segment,
                                ["choice"] = answer.Value.Choice,
                            };
                            if (spec.Value is { } v) row[ColumnName(v.Name)] = answer.Value.Value;
                            foreach (var (key, value) in person.Attributes) row[$"attr_{ColumnName(key)}"] = value;
                            row["reason"] = answer.Value.Reason;
                            rows.Add(row);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }));
                    previous = decided.ToDictionary(kv => kv.Key, kv => kv.Value);
                }
            }
        }

        var ordered = rows.OrderBy(r => (int)r["replication"]!).ThenBy(r => spec.Conditions.ToList().FindIndex(c => c.Name == (string)r["condition"]!))
            .ThenBy(r => (int)r["round"]!).ThenBy(r => (string)r["participant"]!).ToList();
        var summary = Summarize(spec, ordered);
        summary["participants"] = participantsSeen;
        summary["decisions"] = ordered.Count;
        summary["unanswered"] = unanswered;
        summary["stopped_for_cost"] = stoppedForCost;
        summary["cost_usd"] = Math.Round(cost, 4);
        summary["model"] = model;
        return new ExperimentOutcome
        {
            Rows = ordered,
            Summary = summary,
            Participants = participantsSeen,
            Tokens = tokens,
            CostUsd = cost,
            Calls = calls,
            DurationMs = started.ElapsedMilliseconds
        };
    }

    /// <summary>Choice shares per condition and round, the effect of each treatment against the
    /// control (last round), the calibration gap, and a warning when nearly everyone chose alike.</summary>
    public static JsonObject Summarize(ExperimentSpec spec, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var shares = new JsonArray();
        var warnings = new JsonArray();
        var lastRound = rows.Count == 0 ? 1 : rows.Max(r => (int)r["round"]!);
        var finalShares = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (condition, _) in spec.Conditions)
        {
            for (var round = 1; round <= lastRound; round++)
            {
                var these = rows.Where(r => (string)r["condition"]! == condition && (int)r["round"]! == round).ToList();
                if (these.Count == 0) continue;
                var byChoice = spec.Options.ToDictionary(o => o, o => Math.Round(these.Count(r => (string)r["choice"]! == o) / (double)these.Count, 4));
                var entry = new JsonObject { ["condition"] = condition, ["round"] = round, ["n"] = these.Count, ["shares"] = ToJson(byChoice) };
                if (spec.Value is { } v)
                {
                    var values = these.Select(r => r.GetValueOrDefault(ColumnName(v.Name))).OfType<double>().ToList();
                    if (values.Count > 0) entry[$"mean_{ColumnName(v.Name)}"] = Math.Round(values.Average(), 4);
                }

                shares.Add(entry);
                if (round == lastRound) finalShares[condition] = byChoice;
                if (these.Count >= 5 && byChoice.Values.Max() >= 0.9)
                {
                    warnings.Add($"Low diversity: in '{condition}' round {round}, {byChoice.Values.Max():P0} chose the same option. " +
                                 "Real populations rarely agree this much; treat this result with caution.");
                }
            }
        }

        var effects = new JsonArray();
        var control = spec.Conditions[0].Name;
        if (finalShares.TryGetValue(control, out var baseline))
        {
            foreach (var (condition, _) in spec.Conditions.Skip(1))
            {
                if (!finalShares.TryGetValue(condition, out var treated)) continue;
                effects.Add(new JsonObject
                {
                    ["condition"] = condition,
                    ["versus"] = control,
                    ["difference"] = ToJson(spec.Options.ToDictionary(o => o, o => Math.Round(treated[o] - baseline[o], 4)))
                });
            }
        }

        var summary = new JsonObject { ["shares"] = shares, ["effects"] = effects, ["warnings"] = warnings };
        if (spec.Calibration is { } cal && baseline is not null)
        {
            var simulated = baseline.GetValueOrDefault(cal.Option);
            var gap = Math.Round(simulated - cal.Rate, 4);
            summary["calibration"] = new JsonObject
            {
                ["option"] = cal.Option,
                ["real_rate"] = cal.Rate,
                ["simulated_rate"] = simulated,
                ["gap"] = gap,
                ["evidence_id"] = cal.EvidenceId,
                ["calibrated"] = Math.Abs(gap) <= 0.1
            };
            if (Math.Abs(gap) > 0.1)
            {
                warnings.Add($"Calibration gap: under the control condition {simulated:P0} chose '{cal.Option}' against {cal.Rate:P0} in the " +
                             "real data. The simulated population doesn't behave like the real one; weigh its results accordingly.");
            }
        }

        return summary;
    }

    private static JsonObject ToJson(Dictionary<string, double> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values) o[k] = v;
        return o;
    }

    private static LlmToolDefinition DecisionTool(ExperimentSpec spec)
    {
        var properties = new JsonObject
        {
            ["choice"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(spec.Options.Select(o => (JsonNode)o!).ToArray()) },
            ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "One or two sentences, in your own words, why." }
        };
        var required = new JsonArray("choice", "reason");
        if (spec.Value is { } v)
        {
            properties["value"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = $"{v.Name}, between {v.Min.ToString(CultureInfo.InvariantCulture)} and {v.Max.ToString(CultureInfo.InvariantCulture)}"
            };
            required.Add("value");
        }

        return new LlmToolDefinition
        {
            Name = DecideTool,
            Description = "Record your decision.",
            JsonSchema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required }.ToJsonString()
        };
    }

    private static string Prompt(ExperimentSpec spec, Participant person, string condition, string scenario, int round,
        (string Choice, double? Value, string Reason)? mine, IReadOnlyList<string> heard)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Who you are: {person.Description}".TrimEnd());
        if (person.Attributes.Count > 0) sb.AppendLine("About you: " + string.Join("; ", person.Attributes.Select(kv => $"{kv.Key}: {kv.Value}")));
        if (spec.Facts.Count > 0) sb.AppendLine("What's true right now: " + string.Join(" ", spec.Facts.Select(f => f.Fact)));
        sb.AppendLine($"Your situation: {scenario}");
        if (round > 1) sb.AppendLine($"Time has passed (round {round} of {spec.Rounds}).");
        if (mine is { } m) sb.AppendLine($"Last time you chose \"{m.Choice}\" because: {m.Reason}");
        if (heard.Count > 0) sb.AppendLine("What you've heard from others: " + string.Join(" ", heard));
        sb.AppendLine($"Question: {spec.Question}");
        sb.AppendLine($"Options: {string.Join(" / ", spec.Options)}.");
        if (spec.Value is { } v) sb.AppendLine($"Also give {v.Name} (between {v.Min.ToString(CultureInfo.InvariantCulture)} and {v.Max.ToString(CultureInfo.InvariantCulture)}).");
        return sb.ToString();
    }

    private static (string Choice, double? Value, string Reason)? ReadDecision(ExperimentSpec spec, LlmCompletionResponse response)
    {
        var call = response.ToolCalls.FirstOrDefault(c => c.Name == DecideTool);
        if (call is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var root = doc.RootElement;
            var choice = root.TryGetProperty("choice", out var c) ? c.GetString()?.Trim() : null;
            var match = spec.Options.FirstOrDefault(o => string.Equals(o, choice, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            double? value = null;
            if (spec.Value is { } v && root.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Number)
            {
                value = Math.Clamp(val.GetDouble(), v.Min, v.Max);
            }

            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            return (match, value, reason.Length > 400 ? reason[..400] : reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ColumnName(string name)
    {
        var s = new string(name.Trim().ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        while (s.Contains("__")) s = s.Replace("__", "_");
        return s.Length == 0 ? "value" : char.IsDigit(s[0]) ? "v_" + s : s;
    }
}
