using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Infrastructure.Plugins;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests;

/// <summary>
/// The SaaS connectors against a stubbed HTTP layer: the requests they make (URLs, bodies, batching,
/// auth) and how they shape the answers. Not a substitute for trying them against live accounts.
/// </summary>
public sealed class ConnectorPluginTests
{
    /// <summary>Answers requests by URL and records them.</summary>
    private sealed class Stub : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests = [];
        private readonly Func<HttpRequestMessage, string?, (HttpStatusCode, string)> _answer;

        public Stub(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> answer) => _answer = answer;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString()));
            var (status, text) = _answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static PluginConnection Connection(Dictionary<string, string> settings, Dictionary<string, string> secrets) => new()
    {
        ConnectionId = "c1",
        WorkspaceId = "ws-1",
        Name = "conn",
        Settings = settings,
        Secrets = secrets
    };

    private static ToolExecutionRequest Call(string tool, object args) => new()
    {
        ToolName = tool,
        AgentId = "a",
        TaskId = "t",
        ArgumentsJson = JsonSerializer.Serialize(args),
        IdempotencyKey = "run-1:save:0"
    };

    private static string ServiceAccountJson()
    {
        using var rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new
        {
            client_email = "bot@project.iam.gserviceaccount.com",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            token_uri = "https://oauth2.googleapis.com/token",
            project_id = "proj"
        });
    }

    private static (HttpStatusCode, string) GoogleToken() => (HttpStatusCode.OK, """{ "access_token": "ya29.test", "expires_in": 3600 }""");

    // ---- Google Sheets ----

    [Fact]
    public async Task Sheets_reads_rows_as_objects_keyed_by_the_header_row()
    {
        var stub = new Stub((r, _) => r.RequestUri!.Host == "oauth2.googleapis.com" ? GoogleToken()
            : (HttpStatusCode.OK, """{ "range": "Leads!A1:C3", "values": [ ["Name", "Email", ""], ["Ama", "ama@example.com", 5], ["Kofi"] ] }"""));
        var plugin = new GoogleSheetsPlugin(stub, new GoogleServiceAccountTokens(stub));
        var connection = Connection(new() { ["spreadsheet_id"] = "sheet-1" }, new() { ["service_account_json"] = ServiceAccountJson() });

        var result = await plugin.ExecuteToolAsync(connection, "read_rows", Call("read_rows", new { range = "Leads" }));

        Assert.True(result.Success, result.ErrorMessage);
        var rows = JsonNode.Parse(result.ResultJson)!["rows"]!.AsArray();
        Assert.Equal("ama@example.com", rows[0]!["Email"]!.GetValue<string>());
        Assert.Equal(5, rows[0]!["column_3"]!.GetValue<int>());
        Assert.Null(rows[1]!["Email"]);
        Assert.Contains("/spreadsheets/sheet-1/values/Leads?", stub.Requests[1].Url);
        Assert.Equal("Bearer ya29.test", stub.Requests[1].Auth);
        // The signed JWT is exchanged for the token.
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", stub.Requests[0].Body);
    }

    [Fact]
    public async Task Sheets_appends_objects_in_the_header_rows_order_and_refuses_unknown_columns()
    {
        var stub = new Stub((r, _) => r.RequestUri!.Host == "oauth2.googleapis.com" ? GoogleToken()
            : r.Method == HttpMethod.Get ? (HttpStatusCode.OK, """{ "values": [ ["Name", "Email"] ] }""")
            : (HttpStatusCode.OK, """{ "updates": { "updatedRange": "Leads!A5:B5" } }"""));
        var plugin = new GoogleSheetsPlugin(stub, new GoogleServiceAccountTokens(stub));
        var connection = Connection(new() { ["spreadsheet_id"] = "sheet-1", ["allow_writes"] = "true" }, new() { ["service_account_json"] = ServiceAccountJson() });

        var ok = await plugin.ExecuteToolAsync(connection, "append_rows", Call("append_rows", new { range = "Leads", rows = new[] { new { Email = "ama@example.com", Name = "Ama" } } }));
        Assert.True(ok.Success, ok.ErrorMessage);
        var append = stub.Requests.Last();
        Assert.Contains(":append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS", append.Url);
        Assert.Equal("""{"values":[["Ama","ama@example.com"]]}""", append.Body);

        var bad = await plugin.ExecuteToolAsync(connection, "append_rows", Call("append_rows", new { range = "Leads", rows = new[] { new { Phone = "1" } } }));
        Assert.False(bad.Success);
        Assert.Contains("Not columns of 'Leads': Phone", bad.ErrorMessage);
    }

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/1AbC-d_9/edit#gid=0", "1AbC-d_9")]
    [InlineData("https://docs.google.com/spreadsheets/u/0/d/1AbC-d_9/edit?usp=sharing", "1AbC-d_9")]
    [InlineData("  1AbC-d_9  ", "1AbC-d_9")]
    public void Sheets_takes_a_link_or_an_id(string given, string id) => Assert.Equal(id, GoogleSheetsPlugin.SpreadsheetId(given));

    [Fact]
    public async Task Sheets_says_what_to_check_when_a_sheet_is_missing_or_not_shared()
    {
        var status = HttpStatusCode.NotFound;
        var stub = new Stub((r, _) => r.RequestUri!.Host == "oauth2.googleapis.com" ? GoogleToken()
            : (status, """{ "error": { "code": 404, "message": "Requested entity was not found.", "status": "NOT_FOUND" } }"""));
        var plugin = new GoogleSheetsPlugin(stub, new GoogleServiceAccountTokens(stub));
        var connection = Connection(new() { ["spreadsheet_id"] = "https://docs.google.com/spreadsheets/d/1AbC/edit#gid=0" },
            new() { ["service_account_json"] = ServiceAccountJson() });

        var missing = await plugin.ExecuteToolAsync(connection, "read_rows", Call("read_rows", new { range = "Leads" }));
        Assert.Contains("can't find a spreadsheet with id '1AbC'", missing.ErrorMessage);
        Assert.Contains("/spreadsheets/1AbC/values/", stub.Requests.Last().Url);

        status = HttpStatusCode.Forbidden;
        var unshared = await plugin.ExecuteToolAsync(connection, "read_rows", Call("read_rows", new { range = "Leads" }));
        Assert.Contains("Share the sheet with bot@project.iam.gserviceaccount.com", unshared.ErrorMessage);
    }

    [Fact]
    public async Task Sheets_offers_writes_only_when_allowed()
    {
        var stub = new Stub((_, _) => GoogleToken());
        var plugin = new GoogleSheetsPlugin(stub, new GoogleServiceAccountTokens(stub));
        var readOnly = Connection(new(), new() { ["service_account_json"] = ServiceAccountJson() });

        var tools = await plugin.ListToolsAsync(readOnly, default);
        Assert.Equal(["list_sheets", "read_rows"], tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.Equal(ToolSideEffects.ReadOnly, t.SideEffects));
        var refused = await plugin.ExecuteToolAsync(readOnly, "append_rows", Call("append_rows", new { range = "A", rows = Array.Empty<object>() }));
        Assert.Contains("doesn't allow writes", refused.ErrorMessage);
    }

    // ---- BigQuery ----

    [Fact]
    public async Task BigQuery_query_runs_only_selects_and_types_the_rows()
    {
        var statement = "SELECT";
        var stub = new Stub((r, body) => r.RequestUri!.Host == "oauth2.googleapis.com" ? GoogleToken()
            : r.RequestUri.AbsolutePath.EndsWith("/jobs") ? (HttpStatusCode.OK, $$"""{ "statistics": { "query": { "statementType": "{{statement}}" } } }""")
            : (HttpStatusCode.OK, """
                {
                  "jobComplete": true, "totalRows": "2",
                  "schema": { "fields": [ { "name": "name", "type": "STRING" }, { "name": "total", "type": "INTEGER" }, { "name": "vip", "type": "BOOLEAN" } ] },
                  "rows": [ { "f": [ { "v": "Ama" }, { "v": "1200" }, { "v": "true" } ] }, { "f": [ { "v": "Kofi" }, { "v": null }, { "v": "false" } ] } ]
                }
                """));
        var plugin = new BigQueryPlugin(stub, new GoogleServiceAccountTokens(stub));
        var connection = Connection(new() { ["dataset"] = "sales" }, new() { ["service_account_json"] = ServiceAccountJson() });

        var result = await plugin.ExecuteToolAsync(connection, "query", Call("query", new { sql = "SELECT name, total, vip FROM customers WHERE total > @min", @params = new { min = 100 } }));

        Assert.True(result.Success, result.ErrorMessage);
        var rows = JsonNode.Parse(result.ResultJson)!["rows"]!.AsArray();
        Assert.Equal(1200, rows[0]!["total"]!.GetValue<long>());
        Assert.True(rows[0]!["vip"]!.GetValue<bool>());
        Assert.Null(rows[1]!["total"]);
        // The value went in as a typed named parameter, in the project the key names.
        var query = stub.Requests.Last();
        Assert.Contains("/projects/proj/queries", query.Url);
        Assert.Contains("\"parameterType\":{\"type\":\"INT64\"}", query.Body);
        Assert.Contains("\"defaultDataset\":{\"projectId\":\"proj\",\"datasetId\":\"sales\"}", query.Body);

        statement = "DELETE";
        var refused = await plugin.ExecuteToolAsync(connection, "query", Call("query", new { sql = "DELETE FROM customers WHERE true" }));
        Assert.False(refused.Success);
        Assert.Contains("only runs SELECT statements; this is DELETE", refused.ErrorMessage);
    }

    // ---- Airtable ----

    [Fact]
    public async Task Airtable_lists_records_across_pages_and_flattens_them()
    {
        var page = 0;
        var stub = new Stub((_, _) => page++ == 0
            ? (HttpStatusCode.OK, """{ "records": [ { "id": "rec1", "fields": { "Name": "Ama" } } ], "offset": "next" }""")
            : (HttpStatusCode.OK, """{ "records": [ { "id": "rec2", "fields": { "Name": "Kofi" } } ] }"""));
        var plugin = new AirtablePlugin(stub);
        var connection = Connection(new() { ["base_id"] = "appABC123" }, new() { ["api_token"] = "pat-1" });

        var result = await plugin.ExecuteToolAsync(connection, "list_records", Call("list_records", new { table = "Leads", filter_formula = "{Status} = 'Open'", max_records = 50 }));

        Assert.True(result.Success, result.ErrorMessage);
        var records = JsonNode.Parse(result.ResultJson)!["records"]!.AsArray();
        Assert.Equal(["rec1", "rec2"], records.Select(r => r!["id"]!.GetValue<string>()));
        Assert.Equal("Kofi", records[1]!["Name"]!.GetValue<string>());
        Assert.Contains("filterByFormula=%7BStatus%7D%20%3D%20%27Open%27", stub.Requests[0].Url);
        Assert.Contains("&offset=next", stub.Requests[1].Url);
        Assert.Equal("Bearer pat-1", stub.Requests[0].Auth);
    }

    [Fact]
    public async Task Airtable_writes_in_batches_of_ten_and_upserts_merge_on_fields()
    {
        var stub = new Stub((_, body) =>
        {
            var sent = JsonNode.Parse(body!)!["records"]!.AsArray();
            return (HttpStatusCode.OK, new JsonObject { ["records"] = new JsonArray([.. sent.Select((r, i) => (JsonNode?)new JsonObject { ["id"] = $"rec{i}", ["fields"] = r!["fields"]!.DeepClone() })]) }.ToJsonString());
        });
        var plugin = new AirtablePlugin(stub);
        var connection = Connection(new() { ["base_id"] = "appABC123", ["allow_writes"] = "true" }, new() { ["api_token"] = "pat-1" });
        var records = Enumerable.Range(0, 23).Select(i => new { Email = $"p{i}@example.com" }).ToArray();

        var result = await plugin.ExecuteToolAsync(connection, "upsert_records", Call("upsert_records", new { table = "Leads", records, merge_on = new[] { "Email" } }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Patch, r.Method));
        Assert.Contains("\"performUpsert\":{\"fieldsToMergeOn\":[\"Email\"]}", stub.Requests[0].Body);
        Assert.Equal(23, JsonNode.Parse(result.ResultJson)!["count"]!.GetValue<int>());
    }

    // ---- Zoho CRM ----

    [Fact]
    public async Task Zoho_refreshes_its_token_uses_the_api_domain_and_reads_no_content_as_no_matches()
    {
        var stub = new Stub((r, _) => r.RequestUri!.Host == "accounts.zoho.eu"
            ? (HttpStatusCode.OK, """{ "access_token": "1000.tok", "api_domain": "https://www.zohoapis.eu", "expires_in": 3600 }""")
            : (HttpStatusCode.NoContent, ""));
        var plugin = new ZohoCrmPlugin(stub);
        var connection = Connection(new() { ["data_center"] = "eu" }, new() { ["client_id"] = "id", ["client_secret"] = "secret", ["refresh_token"] = "refresh" });

        var result = await plugin.ExecuteToolAsync(connection, "search_records", Call("search_records", new { module = "Leads", email = "ama@example.com" }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, JsonNode.Parse(result.ResultJson)!["count"]!.GetValue<int>());
        Assert.Contains("grant_type=refresh_token", stub.Requests[0].Body);
        Assert.StartsWith("https://www.zohoapis.eu/crm/v8/Leads/search?email=ama%40example.com", stub.Requests[1].Url);
        Assert.Equal("Zoho-oauthtoken 1000.tok", stub.Requests[1].Auth);

        // The token is reused.
        await plugin.ExecuteToolAsync(connection, "search_records", Call("search_records", new { module = "Leads", word = "Ama" }));
        Assert.Single(stub.Requests, r => r.Url.Contains("accounts.zoho.eu"));
    }

    [Fact]
    public async Task Zoho_in_Canada_uses_the_zohocloud_accounts_server()
    {
        var stub = new Stub((r, _) => r.RequestUri!.Host == "accounts.zohocloud.ca"
            ? (HttpStatusCode.OK, """{ "access_token": "t", "api_domain": "https://www.zohoapis.ca", "expires_in": 3600 }""")
            : (HttpStatusCode.OK, """{ "modules": [ { "api_name": "Leads", "plural_label": "Leads" } ] }"""));
        var plugin = new ZohoCrmPlugin(stub);
        var connection = Connection(new() { ["data_center"] = "ca" }, new() { ["client_id"] = "id", ["client_secret"] = "s", ["refresh_token"] = "r" });

        var result = await plugin.ExecuteToolAsync(connection, "list_modules", Call("list_modules", new { }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("https://accounts.zohocloud.ca/oauth/v2/token", stub.Requests[0].Url);
        Assert.StartsWith("https://www.zohoapis.ca/crm/v8/settings/modules", stub.Requests[1].Url);
    }

    [Fact]
    public async Task Zoho_reports_records_that_failed()
    {
        var stub = new Stub((r, _) => r.RequestUri!.Host.StartsWith("accounts.")
            ? (HttpStatusCode.OK, """{ "access_token": "t", "api_domain": "https://www.zohoapis.com" }""")
            : (HttpStatusCode.MultiStatus, """{ "data": [ { "status": "success", "code": "SUCCESS", "action": "insert", "details": { "id": "1" } }, { "status": "error", "code": "MANDATORY_NOT_FOUND", "message": "required field not found", "details": { "api_name": "Last_Name" } } ] }"""));
        var plugin = new ZohoCrmPlugin(stub);
        var connection = Connection(new() { ["allow_writes"] = "true" }, new() { ["client_id"] = "id", ["client_secret"] = "s", ["refresh_token"] = "r" });

        var result = await plugin.ExecuteToolAsync(connection, "create_records", Call("create_records", new { module = "Leads", records = new object[] { new { Last_Name = "Mensah" }, new { Email = "x@example.com" } } }));

        Assert.False(result.Success);
        Assert.Contains("1 record(s) failed (1 succeeded): MANDATORY_NOT_FOUND: required field not found", result.ErrorMessage);
        Assert.Contains("Last_Name", result.ErrorMessage);
    }

    // ---- HTTP API ----

    [Fact]
    public async Task Http_api_results_say_which_request_was_made()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.NotFound, "{}"));
        var plugin = new HttpApiPlugin(stub);
        var connection = Connection(new() { ["base_url"] = "https://api.example.com/v1" }, new() { ["auth_header_value"] = "Bearer secret-token" });

        var result = await plugin.ExecuteToolAsync(connection, "get", Call("get", new { path = "/users/1", query = new { expand = "orders" } }));

        Assert.False(result.Success);
        var payload = JsonNode.Parse(result.ErrorMessage!)!;
        Assert.Equal(404, payload["status"]!.GetValue<int>());
        Assert.Equal("GET https://api.example.com/v1/users/1?expand=orders", payload["request"]!.GetValue<string>());
        Assert.DoesNotContain("secret-token", result.ErrorMessage);
    }

    [Fact]
    public async Task Http_api_endpoints_take_placeholders_with_double_braces_and_refuse_unfilled_ones()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.OK, """{ "id": 1, "name": "Leanne Graham" }"""));
        var plugin = new HttpApiPlugin(stub);
        var endpoints = """[ { "name": "get_user", "method": "GET", "path": "/users/{{user_id}}" } ]""";
        var connection = Connection(new() { ["base_url"] = "https://jsonplaceholder.typicode.com", ["endpoints"] = endpoints }, new());

        var tools = await plugin.ListToolsAsync(connection, default);
        Assert.Contains(tools, t => t.Name == "get_user" && t.JsonSchema.Contains("user_id"));
        var user = await plugin.ExecuteToolAsync(connection, "get_user", Call("get_user", new { user_id = "1" }));
        Assert.True(user.Success, user.ErrorMessage);
        Assert.Equal("https://jsonplaceholder.typicode.com/users/1", stub.Requests.Single().Url);

        // A raw path with a placeholder in it is refused before anything is sent.
        var raw = await plugin.ExecuteToolAsync(connection, "get", Call("get", new { path = "/users/{1}" }));
        Assert.False(raw.Success);
        Assert.Contains("has braces in it", raw.ErrorMessage);
        Assert.Single(stub.Requests);
    }

    // ---- Brevo ----

    [Fact]
    public async Task Brevo_sends_email_with_an_idempotency_key_and_treats_a_duplicate_as_sent()
    {
        var calls = 0;
        var stub = new Stub((_, _) => calls++ == 0
            ? (HttpStatusCode.Created, """{ "messageId": "<1@smtp-relay.mailin.fr>" }""")
            : (HttpStatusCode.BadRequest, """{ "code": "duplicate_parameter", "message": "duplicate idempotency key" }"""));
        var plugin = new BrevoPlugin(stub);
        var connection = Connection(new() { ["sender_email"] = "alerts@example.com", ["sender_name"] = "Ops" }, new() { ["api_key"] = "xkeysib-1" });
        var call = Call("send_email", new { to = "Ada <ada@example.com>, kofi@example.com", subject = "Hi", text = "Hello <there>" });

        var first = await plugin.ExecuteToolAsync(connection, "send_email", call);
        var again = await plugin.ExecuteToolAsync(connection, "send_email", call);

        Assert.True(first.Success, first.ErrorMessage);
        Assert.True(again.Success, again.ErrorMessage);
        var sent = JsonNode.Parse(stub.Requests[0].Body!)!;
        Assert.Equal("https://api.brevo.com/v3/smtp/email", stub.Requests[0].Url);
        Assert.Equal("Ada", sent["to"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("kofi@example.com", sent["to"]![1]!["email"]!.GetValue<string>());
        Assert.Contains("Hello &lt;there&gt;", sent["htmlContent"]!.GetValue<string>());
        // The same call carries the same UUID key, so Brevo drops the repeat.
        Assert.Equal(sent["headers"]!["idempotencyKey"]!.GetValue<string>(), JsonNode.Parse(stub.Requests[1].Body!)!["headers"]!["idempotencyKey"]!.GetValue<string>());
        Assert.True(Guid.TryParse(sent["headers"]!["idempotencyKey"]!.GetValue<string>(), out _));
    }

    [Fact]
    public async Task Brevo_sends_sms_and_alerts_by_the_chosen_channel()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.Created, """{ "messageId": 1511882900100020 }"""));
        var plugin = new BrevoPlugin(stub);
        var connection = Connection(new() { ["sms_sender"] = "Aktor", ["notify_by"] = "sms", ["notify_phone"] = "+233201234567" }, new() { ["api_key"] = "xkeysib-1" });

        var sms = await plugin.ExecuteToolAsync(connection, "send_sms", Call("send_sms", new { to = "+15551234567", text = "Stock is low" }));
        Assert.True(sms.Success, sms.ErrorMessage);
        Assert.Equal("https://api.brevo.com/v3/transactionalSMS/send", stub.Requests[0].Url);
        Assert.Equal("15551234567", JsonNode.Parse(stub.Requests[0].Body!)!["recipient"]!.GetValue<string>());

        var alert = await plugin.SendNotificationAsync(connection, new OutboundNotification { DeliveryId = "d1", WorkspaceName = "Shop", AuthorName = "Run #3", Text = "Done." }, default);
        Assert.True(alert.Delivered);
        Assert.Contains("[Shop] Done.", JsonNode.Parse(stub.Requests[1].Body!)!["content"]!.GetValue<string>());

        // Without an SMS sender: refused clearly, not retried.
        var unset = Connection(new(), new() { ["api_key"] = "xkeysib-1" });
        var refused = await plugin.ExecuteToolAsync(unset, "send_sms", Call("send_sms", new { to = "+1", text = "x" }));
        Assert.Contains("Set an SMS sender", refused.ErrorMessage);
    }

    // ---- SendGrid ----

    [Fact]
    public async Task SendGrid_sends_mail_and_reports_rejections_as_not_retryable()
    {
        var reject = false;
        var stub = new Stub((_, _) => reject
            ? (HttpStatusCode.BadRequest, """{ "errors": [ { "message": "The from address does not match a verified Sender Identity." } ] }""")
            : (HttpStatusCode.Accepted, ""));
        var plugin = new SendGridPlugin(stub);
        var connection = Connection(new() { ["from_email"] = "alerts@example.com", ["notify_to"] = "me@example.com" }, new() { ["api_key"] = "SG.x" });

        var sent = await plugin.ExecuteToolAsync(connection, "send_email", Call("send_email", new { to = "ada@example.com", subject = "Hi", html = "<b>Hello</b>" }));
        Assert.True(sent.Success, sent.ErrorMessage);
        var body = JsonNode.Parse(stub.Requests[0].Body!)!;
        Assert.Equal("https://api.sendgrid.com/v3/mail/send", stub.Requests[0].Url);
        Assert.Equal("Bearer SG.x", stub.Requests[0].Auth);
        Assert.Equal("ada@example.com", body["personalizations"]![0]!["to"]![0]!["email"]!.GetValue<string>());
        Assert.Equal("<b>Hello</b>", body["content"]![0]!["value"]!.GetValue<string>());

        reject = true;
        var alert = await plugin.SendNotificationAsync(connection, new OutboundNotification { DeliveryId = "d1", WorkspaceName = "Shop", AuthorName = "Run #3", Text = "Done." }, default);
        Assert.False(alert.Delivered);
        Assert.False(alert.Retryable);
    }

    // ---- Sentry ----

    [Fact]
    public async Task Sentry_lists_issues_and_trims_an_event_to_the_apps_frames()
    {
        var stub = new Stub((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/events/latest/")
            ? (HttpStatusCode.OK, """
                {
                  "eventID": "e1", "title": "ZeroDivisionError: division by zero", "dateCreated": "2026-10-13T10:00:00Z", "platform": "python",
                  "release": { "version": "web@1.4.2" },
                  "tags": [ { "key": "environment", "value": "production" }, { "key": "browser", "value": "Chrome" } ],
                  "entries": [
                    { "type": "exception", "data": { "values": [ { "type": "ZeroDivisionError", "value": "division by zero", "stacktrace": { "frames": [
                      { "filename": "django/core/handlers.py", "function": "inner", "lineNo": 47, "inApp": false },
                      { "filename": "shop/views.py", "function": "checkout", "lineNo": 12, "inApp": true, "context": [[11, "  total = 0"], [12, "  avg = sum / total"]] }
                    ] } } ] } },
                    { "type": "request", "data": { "method": "POST", "url": "https://shop.example.com/checkout" } }
                  ]
                }
                """)
            : (HttpStatusCode.OK, """
                [ { "id": "42", "shortId": "SHOP-1A", "title": "ZeroDivisionError", "culprit": "shop.views in checkout", "level": "error", "status": "unresolved",
                    "count": "17", "userCount": 5, "firstSeen": "2026-10-12T09:00:00Z", "lastSeen": "2026-10-13T10:00:00Z",
                    "project": { "slug": "shop" }, "permalink": "https://sentry.io/organizations/acme/issues/42/" } ]
                """));
        var plugin = new SentryPlugin(stub);
        var connection = Connection(new() { ["organization"] = "acme" }, new() { ["auth_token"] = "sntrys_1" });

        var issues = await plugin.ExecuteToolAsync(connection, "list_issues", Call("list_issues", new { query = "is:unresolved level:error", period = "14d" }));
        Assert.True(issues.Success, issues.ErrorMessage);
        Assert.Equal("SHOP-1A", JsonNode.Parse(issues.ResultJson)!["issues"]![0]!["short_id"]!.GetValue<string>());
        Assert.StartsWith("https://sentry.io/api/0/organizations/acme/issues/?query=is%3Aunresolved%20level%3Aerror&statsPeriod=14d&sort=date", stub.Requests[0].Url);
        Assert.Equal("Bearer sntrys_1", stub.Requests[0].Auth);

        var latest = JsonNode.Parse((await plugin.ExecuteToolAsync(connection, "get_latest_event", Call("get_latest_event", new { issue_id = "42" }))).ResultJson)!;
        var frame = Assert.Single(latest["exceptions"]![0]!["frames"]!.AsArray());
        Assert.Equal("shop/views.py", frame!["file"]!.GetValue<string>());
        Assert.Equal("avg = sum / total", frame["code"]!.GetValue<string>());
        Assert.Equal("production", latest["environment"]!.GetValue<string>());
        Assert.Equal("web@1.4.2", latest["release"]!.GetValue<string>());

        // Changes need the connection to allow them.
        Assert.DoesNotContain(await plugin.ListToolsAsync(connection, default), t => t.Name == "update_issue");
        Assert.False((await plugin.ExecuteToolAsync(connection, "update_issue", Call("update_issue", new { issue_id = "42", status = "resolved" }))).Success);
    }

    // ---- Slack ----

    [Fact]
    public async Task Slack_with_a_bot_token_posts_to_channels_and_reads_them_by_name()
    {
        var stub = new Stub((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/api/chat.postMessage" => (HttpStatusCode.OK, """{ "ok": true, "channel": "C1", "ts": "1700.01" }"""),
            "/api/conversations.list" => (HttpStatusCode.OK, """{ "ok": true, "channels": [ { "id": "C1", "name": "alerts", "is_member": true } ], "response_metadata": { "next_cursor": "" } }"""),
            "/api/conversations.history" => (HttpStatusCode.OK, """{ "ok": true, "messages": [ { "ts": "1700.00", "user": "U1", "text": "deploy failed" } ] }"""),
            _ => (HttpStatusCode.OK, """{ "ok": false, "error": "unknown_method" }""")
        });
        var plugin = new SlackPlugin(stub);
        var connection = Connection(new() { ["default_channel"] = "#alerts" }, new() { ["bot_token"] = "xoxb-1" });

        Assert.Equal(["post_message", "list_channels", "read_messages"], (await plugin.ListToolsAsync(connection, default)).Select(t => t.Name));

        var posted = await plugin.ExecuteToolAsync(connection, "post_message", Call("post_message", new { text = "Run done", thread_ts = "1700.00" }));
        Assert.True(posted.Success, posted.ErrorMessage);
        Assert.Equal("1700.01", JsonNode.Parse(posted.ResultJson)!["ts"]!.GetValue<string>());
        var post = JsonNode.Parse(stub.Requests[0].Body!)!;
        Assert.Equal("#alerts", post["channel"]!.GetValue<string>());
        Assert.Equal("1700.00", post["thread_ts"]!.GetValue<string>());
        Assert.Equal("Bearer xoxb-1", stub.Requests[0].Auth);

        // History takes an id, so a #name is looked up first.
        var read = await plugin.ExecuteToolAsync(connection, "read_messages", Call("read_messages", new { channel = "#alerts", limit = 5 }));
        Assert.True(read.Success, read.ErrorMessage);
        Assert.Equal("deploy failed", JsonNode.Parse(read.ResultJson)!["messages"]![0]!["text"]!.GetValue<string>());
        Assert.EndsWith("conversations.history?channel=C1&limit=5", stub.Requests[^1].Url);
    }

    [Fact]
    public async Task Slack_explains_errors_it_answers_with_200()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.OK, """{ "ok": false, "error": "not_in_channel" }"""));
        var plugin = new SlackPlugin(stub);
        var connection = Connection([], new() { ["bot_token"] = "xoxb-1" });

        var result = await plugin.ExecuteToolAsync(connection, "post_message", Call("post_message", new { text = "hi", channel = "C9" }));
        Assert.False(result.Success);
        Assert.Contains("/invite", result.ErrorMessage);

        // No channel given and no default: an argument mistake, not a request.
        var missing = await plugin.ExecuteToolAsync(connection, "post_message", Call("post_message", new { text = "hi" }));
        Assert.False(missing.Success);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Slack_with_only_a_webhook_posts_there_and_offers_nothing_else()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.OK, "ok"));
        var plugin = new SlackPlugin(stub);
        var connection = Connection([], new() { ["webhook_url"] = "https://hooks.slack.com/services/T/B/x" });

        Assert.True((await plugin.ValidateAsync(connection, default)).Ok);
        Assert.Equal(["post_message"], (await plugin.ListToolsAsync(connection, default)).Select(t => t.Name));
        Assert.True((await plugin.ExecuteToolAsync(connection, "post_message", Call("post_message", new { text = "hi" }))).Success);
        Assert.Equal("https://hooks.slack.com/services/T/B/x", stub.Requests[0].Url);
        Assert.False((await plugin.ExecuteToolAsync(connection, "list_channels", Call("list_channels", new { }))).Success);

        Assert.False((await plugin.ValidateAsync(Connection([], []), default)).Ok);
    }
}
