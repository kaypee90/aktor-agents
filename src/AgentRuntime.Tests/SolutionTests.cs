using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Secrets;
using AgentRuntime.Infrastructure.Solutions;
using AgentRuntime.Solutions;
using AgentRuntime.Workspaces;

namespace AgentRuntime.Tests;

/// <summary>Solutions (docs/solutions.md): packages, data model rules, record triggers, tokens.</summary>
public class SolutionTests
{
    private static string RepoRoot()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "AktorAgents.slnx"))) root = Path.GetDirectoryName(root);
        return root ?? throw new InvalidOperationException("Can't find the repository root.");
    }

    private static readonly HashSet<string> KnownPlugins = ["twilio-sms", "slack", "aktor-records", "mcp"];

    private static SolutionValidation Validate(SolutionManifest m, Dictionary<string, string>? files = null) =>
        SolutionValidator.Validate(m, KnownPlugins.Contains, new Pipelines.PipelineOptions(), files);

    private static (SolutionManifest Manifest, List<string> Errors) Read(string yaml, Dictionary<string, string>? files = null)
    {
        var (m, errors) = SolutionManifestReader.Read(new SolutionBundle { Manifest = yaml, Files = files ?? [] });
        return (m!, errors);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---------------------------------------------------------------- the sample package

    [Fact]
    public void SamplePropertyManagementPackage_ReadsAndValidates()
    {
        var bundle = SolutionPackage.FromDirectory(Path.Combine(RepoRoot(), "samples", "solutions", "property-mgmt"));
        var (manifest, errors) = SolutionManifestReader.Read(bundle);

        Assert.Empty(errors);
        Assert.NotNull(manifest);
        Assert.Equal("property-mgmt", manifest!.Name);
        Assert.Equal(["maintenance-desk", "lease-renewals"], manifest.Workspaces.Select(w => w.Key));
        Assert.Equal(["Property", "Lease", "Ticket"], manifest.Entities.Keys);
        // Included files: workspace files, the assistant's instructions_file, eval files.
        Assert.Contains("data__create_ticket", manifest.Assistant!.Instructions);
        Assert.Equal(TriggerKind.Record, manifest.Workspaces[0].Triggers[0].Kind);
        Assert.Equal(["created"], manifest.Workspaces[0].Triggers[0].On);
        var watch = manifest.Workspaces[1].Triggers[0];
        Assert.Equal(TriggerKind.Watch, watch.Kind);
        // source_arguments written as YAML become the JSON string watches take.
        Assert.Contains("today+60d", watch.SourceArgumentsJson);
        Assert.Single(manifest.Evals);

        var validation = SolutionValidator.Validate(manifest, KnownPlugins.Contains, new Pipelines.PipelineOptions(), SolutionManifestReader.Files(bundle));
        Assert.True(validation.Ok, string.Join("\n", validation.Errors));
    }

    [Fact]
    public void EverySamplePackage_ReadsAndValidates()
    {
        // The compatibility check for this platform version: every published sample still installs.
        var root = Path.Combine(RepoRoot(), "samples", "solutions");
        var packages = Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "aktor.yaml"))).ToList();
        Assert.True(packages.Count >= 2, "Expected at least two sample solutions.");
        foreach (var dir in packages)
        {
            var bundle = SolutionPackage.FromDirectory(dir);
            var (manifest, errors) = SolutionManifestReader.Read(bundle);
            Assert.True(errors.Count == 0, $"{Path.GetFileName(dir)}: {string.Join("; ", errors)}");
            var v = SolutionValidator.Validate(manifest!, KnownPlugins.Contains, new Pipelines.PipelineOptions(), SolutionManifestReader.Files(bundle));
            Assert.True(v.Ok, $"{Path.GetFileName(dir)}: {string.Join("; ", v.Errors)}");
        }
    }

    [Theory]
    [InlineData(">=1.0.0", true)]
    [InlineData(">=1.1.0", true)]
    [InlineData(">=1.2.0", true)]
    [InlineData(">=1.3.0", true)]
    [InlineData(">=1.4.0", true)]
    [InlineData(">=1.5.0", false)]
    [InlineData("^1.0.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("^2.0.0", false)]
    [InlineData("=1.0.0", false)]
    [InlineData(">1.4.0", false)]
    public void RequiresPlatform_IsCheckedAgainstThisServer(string requirement, bool ok) =>
        Assert.Equal(ok, SolutionValidator.PlatformMismatch(requirement) is null);

    [Fact]
    public void AssistantOrigins_MustBeBareOrigins()
    {
        var (m, _) = Read("""
            name: shop
            version: 1.0.0
            assistant:
              instructions: Help.
              allowed_origins: ["https://app.example.com", "https://app.example.com/path", "app.example.com"]
            """);
        var errors = Validate(m).Errors.Where(e => e.Contains("allowed origin")).ToList();
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void BrokenYaml_IsAnErrorWithItsLine_NotACrash()
    {
        var (m, errors) = Read("""
            name: shop
            version: 1.0.0
            inputs:
              broken: [unclosed
            """);
        Assert.Null(m);
        Assert.StartsWith("aktor.yaml: The YAML couldn't be read: (Line: ", Assert.Single(errors));

        // YamlDotNet gives up on this one with InvalidOperationException, which was a 500.
        var (_, unclosed) = Read("""
            name: shop
            inputs:
              broken: [unclosed
              more: value
            version: 1.0.0
            """);
        Assert.Contains("(Line: ", Assert.Single(unclosed));
    }

    [Fact]
    public void AssistantOrigins_AcceptAnyOriginForTesting()
    {
        var (m, _) = Read("""
            name: shop
            version: 1.0.0
            assistant:
              instructions: Help.
              allowed_origins: ["*"]
            """);
        var validation = Validate(m);
        Assert.DoesNotContain(validation.Errors, e => e.Contains("allowed origin"));
        Assert.Contains(validation.Warnings, w => w.Contains("'*'") && w.Contains("testing"));
    }

    [Fact]
    public void AssistantOrigins_WarnWhenEmptyOrNotHttps()
    {
        var (open, _) = Read("""
            name: shop
            version: 1.0.0
            assistant:
              instructions: Help.
            """);
        Assert.Contains(Validate(open).Warnings, w => w.Contains("allowed_origins is empty"));

        var (plain, _) = Read("""
            name: shop
            version: 1.0.0
            assistant:
              instructions: Help.
              allowed_origins: ["http://app.example.com", "http://localhost:3001", "https://app.example.com"]
            """);
        var warnings = Validate(plain).Warnings;
        Assert.Contains(warnings, w => w.Contains("'http://app.example.com' isn't https"));
        Assert.DoesNotContain(warnings, w => w.Contains("localhost") || w.Contains("allowed_origins is empty"));
    }

    [Theory]
    [InlineData("templates", false)]
    [InlineData("validate", false)]
    [InlineData("templates-app", true)]
    public void Names_the_api_uses_as_routes_are_reserved(string name, bool ok)
    {
        var (m, _) = Read($"""
            name: {name}
            version: 1.0.0
            assistant:
              instructions: Help.
            """);
        Assert.Equal(ok, !Validate(m).Errors.Any(e => e.Contains("the API uses that word")));
    }

    [Fact]
    public void RecordQueries_BecomeParameterizedSqlOverTheWholeTable()
    {
        var scope = new RecordScope("t1", "sol-1", "u-1");
        var query = new RecordQuery
        {
            Where = new() { ["status"] = "ACTIVE" },
            Filters =
            [
                new WatchCondition { Field = "rent", Op = ">=", Value = "1000" },
                new WatchCondition { Field = "ends_on", Op = "<=", Value = "today+30d" },
                new WatchCondition { Field = "unit", Op = "contains", Value = "4%" }
            ],
            OrderBy = "rent",
            Descending = false,
            Limit = 500
        };
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var cmd = RecordQuerySql.Build(scope, "Lease", Lease, query, now, page: true);
        var values = cmd.Parameters.Cast<Npgsql.NpgsqlParameter>().Select(p => p.Value).ToList();

        // Scope first, always; field names and values only as parameters.
        Assert.StartsWith("SELECT * FROM \"SolutionRecords\" WHERE \"TenantId\" = @q0 AND \"InstallId\" = @q1 AND \"Entity\" = @q2 AND \"EndUserId\" = @q3", cmd.Sql);
        // Field names are checked, then written as literals so per-field indexes match.
        Assert.Contains("'rent'", cmd.Sql);
        Assert.Contains("""{"status":"active"}""", values.OfType<string>());   // typed, normalized enum
        Assert.Contains(1000m, values.OfType<decimal>());
        Assert.Contains("2026-11-15", values.OfType<string>());                 // relative date resolved
        Assert.Contains("%4\\%%", values.OfType<string>());                    // wildcards escaped
        Assert.Contains(200, values.OfType<int>());                              // limit capped
        Assert.Contains("ORDER BY (CASE WHEN jsonb_typeof", cmd.Sql);            // numeric sort

        // Only the entity's own fields; numbers must be numbers.
        Assert.Throws<ArgumentException>(() => RecordQuerySql.Build(scope, "Lease", Lease,
            new RecordQuery { Filters = [new WatchCondition { Field = "password", Op = "==", Value = "x" }] }, now, page: false));
        Assert.Throws<ArgumentException>(() => RecordQuerySql.Build(scope, "Lease", Lease,
            new RecordQuery { Filters = [new WatchCondition { Field = "rent", Op = ">", Value = "lots" }] }, now, page: false));
        Assert.Throws<ArgumentException>(() => RecordQuerySql.Build(scope, "Lease", Lease, new RecordQuery { OrderBy = "nope" }, now, page: true));

        // Without an end user (a backend, or an entity without an end_user field), no owner filter.
        Assert.DoesNotContain("EndUserId", RecordQuerySql.Build(new RecordScope("t1", "sol-1"), "Lease", Lease, new RecordQuery(), now, page: false).Sql);
    }

    // ---------------------------------------------------------------- manifest validation

    private const string Minimal = """
        name: shop
        version: 1.0.0
        entities:
          Order:
            fields:
              total: { type: number, required: true }
              status: { type: enum, values: [new, paid] }
        workspaces:
          - key: orders
            name: Orders
            goal: Handle orders
            data: { Order: write }
        """;

    [Fact]
    public void MinimalManifest_IsValid()
    {
        var (m, errors) = Read(Minimal);
        Assert.Empty(errors);
        Assert.True(Validate(m).Ok, string.Join("\n", Validate(m).Errors));
    }

    [Fact]
    public void Validation_ListsEveryProblem()
    {
        var (m, errors) = Read("""
            name: Bad_Name
            version: one
            inputs:
              - { key: Fee }
            connectors:
              - { name: data, plugin: slack }
              - { name: sms, plugin: nope }
            entities:
              order:
                fields:
                  id: { type: string }
                  owner: { type: ref }
            workspaces:
              - key: a
                name: A
                goal: g
                connectors: [missing]
                data: { Order: admin }
                triggers:
                  - { kind: Record, name: t, entity: Ghost }
                  - { kind: Record, name: t, entity: Ghost, on: [renamed] }
            """);
        Assert.Empty(errors);
        var v = Validate(m);
        Assert.False(v.Ok);
        string[] expected =
        [
            "'name' is required", "'version' is required", "Input 'Fee'", "'data' is the solution's own records connection",
            "no plugin 'nope'", "Entity 'order': names are PascalCase", "'id' is kept for the runtime", "a ref needs 'entity'",
            "uses connector 'missing'", "access to 'Order' is 'read' or 'write'", "'Ghost' isn't an entity", "trigger names must be unique", "'renamed' isn't a record change"
        ];
        foreach (var e in expected) Assert.Contains(v.Errors, x => x.Contains(e, StringComparison.Ordinal));
    }

    [Fact]
    public void Reader_ReportsMissingIncludedFiles_AndBadPaths()
    {
        var (_, errors) = Read("""
            name: shop
            version: 1.0.0
            workspaces:
              - file: workspaces/missing.yaml
            """);
        Assert.Contains(errors, e => e.Contains("'workspaces/missing.yaml' isn't in the package"));

        var (none, pathErrors) = SolutionManifestReader.Read(new SolutionBundle { Manifest = "name: x", Files = new() { ["../etc/passwd"] = "x" } });
        Assert.Null(none);
        Assert.Contains(pathErrors, e => e.Contains("relative, without '..'"));
    }

    [Fact]
    public void Inputs_AreResolvedWithDefaultsTypesAndOptions()
    {
        var (m, _) = Read("""
            name: shop
            version: 1.0.0
            inputs:
              - { key: fee, type: number, default: "5" }
              - { key: region, required: true, options: [eu, us] }
              - { key: live, type: boolean }
            workspaces: [{ key: a, name: A, goal: g }]
            """);
        var (values, errors) = SolutionValidator.ResolveInputs(m, new Dictionary<string, string> { ["region"] = "asia", ["live"] = "maybe", ["extra"] = "1" });
        Assert.Contains(errors, e => e.Contains("one of eu, us"));
        Assert.Contains(errors, e => e.Contains("true or false"));
        Assert.Contains(errors, e => e.Contains("'extra' isn't an input"));

        (values, errors) = SolutionValidator.ResolveInputs(m, new Dictionary<string, string> { ["region"] = "eu" });
        Assert.Empty(errors);
        Assert.Equal("5", values["fee"]);
        Assert.Equal("eu", values["region"]);
        Assert.False(values.ContainsKey("live"));

        // On an upgrade, values given before are kept.
        (values, _) = SolutionValidator.ResolveInputs(m, null, previous: new Dictionary<string, string> { ["region"] = "us", ["fee"] = "7" });
        Assert.Equal("us", values["region"]);
        Assert.Equal("7", values["fee"]);

        Assert.Equal("https://api.example.com/hook?r=eu",
            SolutionValidator.ReplaceInputs("https://api.example.com/hook?r={{ inputs.region }}", new Dictionary<string, string> { ["region"] = "eu" }));
    }

    [Theory]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    public void Versions_CompareSemantically(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SolutionValidator.CompareVersions(a, b)));

    // ---------------------------------------------------------------- records

    private static readonly EntityDefinition Lease = new()
    {
        Fields = new()
        {
            ["unit"] = new FieldDefinition { Type = FieldTypes.String, Required = true },
            ["rent"] = new FieldDefinition { Type = FieldTypes.Number },
            ["rooms"] = new FieldDefinition { Type = FieldTypes.Integer },
            ["ends_on"] = new FieldDefinition { Type = FieldTypes.Date },
            ["furnished"] = new FieldDefinition { Type = FieldTypes.Boolean },
            ["status"] = new FieldDefinition { Type = FieldTypes.Enum, Values = ["active", "ended"], Default = JsonValue.Create("active") },
            ["resident"] = new FieldDefinition { Type = FieldTypes.EndUser }
        }
    };

    [Fact]
    public void RecordSchema_CoercesAndNormalizesValues()
    {
        var (data, errors) = RecordSchema.Apply(Lease, new JsonObject
        {
            ["unit"] = "4B", ["rent"] = "1450.50", ["rooms"] = 3.0, ["ends_on"] = "2026-12-31T00:00:00Z", ["furnished"] = "yes", ["status"] = "ENDED"
        }, null, endUserId: null);

        Assert.Empty(errors);
        Assert.Equal(1450.50m, data!["rent"]!.GetValue<decimal>());
        Assert.Equal(3L, data["rooms"]!.GetValue<long>());
        Assert.Equal("2026-12-31", data["ends_on"]!.GetValue<string>());
        Assert.True(data["furnished"]!.GetValue<bool>());
        Assert.Equal("ended", data["status"]!.GetValue<string>());
    }

    [Fact]
    public void RecordSchema_RefusesUnknownFieldsWrongTypesAndMissingRequired()
    {
        var (data, errors) = RecordSchema.Apply(Lease, new JsonObject { ["rent"] = "a lot", ["status"] = "pending", ["colour"] = "red" }, null, null);
        Assert.Null(data);
        Assert.Contains(errors, e => e.StartsWith("'colour' isn't a field"));
        Assert.Contains(errors, e => e.StartsWith("'rent': expected a number"));
        Assert.Contains(errors, e => e.StartsWith("'status': expected one of active, ended"));
        Assert.Contains("'unit' is required.", errors);
    }

    [Fact]
    public void RecordSchema_TheEndUserFieldIsTheRuntimes()
    {
        // In an end user's conversation, the owner is that end user, whatever the writer sends.
        var (data, _) = RecordSchema.Apply(Lease, new JsonObject { ["unit"] = "1", ["resident"] = "someone-else" }, null, endUserId: "u-1");
        Assert.Equal("u-1", data!["resident"]!.GetValue<string>());
        Assert.Equal("active", data["status"]!.GetValue<string>()); // default

        // A backend (no end user) may set it on create...
        (data, _) = RecordSchema.Apply(Lease, new JsonObject { ["unit"] = "1", ["resident"] = "u-2" }, null, endUserId: null);
        Assert.Equal("u-2", data!["resident"]!.GetValue<string>());

        // ...but nobody changes it afterwards.
        var (updated, errors) = RecordSchema.Apply(Lease, new JsonObject { ["resident"] = "u-3", ["rent"] = 10 }, data, endUserId: null);
        Assert.Empty(errors);
        Assert.Equal("u-2", updated!["resident"]!.GetValue<string>());
        Assert.Equal(10m, updated["rent"]!.GetValue<decimal>());

        // Null clears a field on update.
        (updated, _) = RecordSchema.Apply(Lease, new JsonObject { ["rent"] = null }, updated, null);
        Assert.False(updated!.ContainsKey("rent"));
    }

    [Fact]
    public void FieldsSchema_DescribesWhatAWriterMaySet()
    {
        var schema = RecordSchema.FieldsSchema(Lease, forUpdate: false);
        Assert.Equal("number", schema["properties"]!["rent"]!["type"]!.GetValue<string>());
        Assert.Null(schema["properties"]!["resident"]); // the runtime's
        Assert.Equal(["unit"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(RecordSchema.FieldsSchema(Lease, forUpdate: true)["required"]);
        Assert.Equal("work_order", RecordSchema.ToolSlug("WorkOrder"));
    }

    [Fact]
    public void RelativeDates_ResolveAgainstNow()
    {
        var now = new DateTimeOffset(2026, 10, 16, 15, 30, 0, TimeSpan.Zero);
        Assert.Equal("2026-10-16", RelativeDates.Resolve("today", now));
        Assert.Equal("2026-12-15", RelativeDates.Resolve("today+60d", now));
        Assert.Equal("2026-10-09", RelativeDates.Resolve("today - 1w", now));
        Assert.Equal("2026-10-16T17:30:00Z", RelativeDates.Resolve("now+2h", now));
        Assert.Equal("2026-01-01", RelativeDates.Resolve("2026-01-01", now));
    }

    // ---------------------------------------------------------------- record triggers

    [Fact]
    public void RecordTriggers_MatchEntityEventAndConditions()
    {
        var trigger = new TriggerDefinition
        {
            TriggerId = "t", Kind = TriggerKind.Record, Name = "Urgent tickets", Entity = "Ticket", RecordEvents = ["created", "updated"],
            Rule = new WatchRule { Conditions = [new WatchCondition { Field = "priority", Op = "==", Value = "urgent" }] }
        };
        RecordChange Change(string entity, string evt, string priority) => new()
        {
            TenantId = "t1", InstallId = "sol-1", Entity = entity, RecordId = "rec-1", Event = evt, DataJson = $$"""{"id":"rec-1","priority":"{{priority}}"}"""
        };

        Assert.True(RecordTriggers.Matches(trigger, Change("Ticket", "created", "urgent")));
        Assert.True(RecordTriggers.Matches(trigger, Change("Ticket", "updated", "urgent")));
        Assert.False(RecordTriggers.Matches(trigger, Change("Ticket", "deleted", "urgent")));
        Assert.False(RecordTriggers.Matches(trigger, Change("Ticket", "created", "low")));
        Assert.False(RecordTriggers.Matches(trigger, Change("Lease", "created", "urgent")));

        Assert.Equal(["created"], RecordTriggers.Events(new TriggerSpec { Kind = TriggerKind.Record, Name = "x", Entity = "Ticket" }));
        Assert.NotNull(RecordTriggers.Validate(new TriggerSpec { Kind = TriggerKind.Record, Name = "x" }));
    }

    // ---------------------------------------------------------------- tokens

    [Fact]
    public void RecordsBindings_AreSignedAndTamperProof()
    {
        var tokens = new SolutionTokens(new SecretProtector(new byte[32]), TimeProvider.System);
        var signed = tokens.SignBinding(new RecordsBinding("t1", "sol-1", "u-1", new() { ["Ticket"] = "write" }));
        var binding = tokens.VerifyBinding(signed);
        Assert.Equal(("t1", "sol-1", "u-1"), (binding!.TenantId, binding.InstallId, binding.EndUserId));
        Assert.Equal("write", binding.Access["Ticket"]);

        var parts = signed.Split('.');
        var forged = tokens.SignBinding(new RecordsBinding("t2", "sol-1", null, new() { ["Ticket"] = "write" })).Split('.')[1];
        Assert.Null(tokens.VerifyBinding($"v1.{forged}.{parts[2]}"));
        // Another key can't verify it.
        Assert.Null(new SolutionTokens(new SecretProtector(Enumerable.Repeat((byte)1, 32).ToArray()), TimeProvider.System).VerifyBinding(signed));
    }

    [Fact]
    public void EndUserTokens_ExpireAndCantBeUsedAsBindings()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var protector = new SecretProtector(new byte[32]);
        var tokens = new SolutionTokens(protector, clock);
        var token = tokens.IssueEndUserToken("t1", "sol-1", "resident-4b", "Sam", TimeSpan.FromMinutes(30));

        Assert.StartsWith("aeu_", token);
        var claims = tokens.VerifyEndUserToken(token);
        Assert.Equal(("t1", "sol-1", "resident-4b", "Sam"), (claims!.TenantId, claims.InstallId, claims.UserId, claims.Name));

        // A signature for one purpose never verifies for another.
        var payload = token[4..].Split('.')[0];
        Assert.Null(tokens.VerifyBinding($"v1.{payload}.{token.Split('.')[1]}"));
        Assert.Null(tokens.VerifyEndUserToken(token[..^2] + "xx"));

        clock.Now = clock.Now.AddMinutes(31);
        Assert.Null(tokens.VerifyEndUserToken(token));
    }
}
