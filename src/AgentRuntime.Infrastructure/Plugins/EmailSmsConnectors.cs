using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

/// <summary>Shared pieces of the email and SMS services' APIs.</summary>
internal static class Messaging
{
    /// <summary>A failed send: 4xx (bad address, bad key) won't get better by retrying; 429 and 5xx might.</summary>
    public static DeliveryResult Failure(string service, int status, JsonNode? body, string text) =>
        DeliveryResult.Failed(Connector.Fail(service, status, body, text).ErrorMessage!, retryable: status is 429 or >= 500);

    /// <summary>A UUID that's the same for the same idempotency key (services that want UUIDs).</summary>
    public static string Uuid(string key) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToString();

    /// <summary>A notification's subject line: the workspace, urgency, and the first line of the text.</summary>
    public static string Subject(OutboundNotification n) =>
        $"[{n.WorkspaceName}] {(n.Urgency == "urgent" ? "URGENT: " : string.Empty)}{MessagingText.Truncate(n.Text.Split('\n')[0], 70)}";

    public static string Body(OutboundNotification n) => $"{n.AuthorName} says:\n\n{n.Text}";

    public static string Html(string text) => "<div style=\"font-family:sans-serif;white-space:pre-wrap\">" + WebUtility.HtmlEncode(text) + "</div>";

    /// <summary>"Ada &lt;ada@example.com&gt;" or "ada@example.com" → (email, name).</summary>
    public static (string Email, string? Name) Address(string value)
    {
        var v = value.Trim();
        var open = v.LastIndexOf('<');
        return open > 0 && v.EndsWith('>') ? (v[(open + 1)..^1].Trim(), v[..open].Trim().Trim('"')) : (v, null);
    }

    /// <summary>One address, or a comma-separated list, or a JSON list.</summary>
    public static List<string> Recipients(JsonNode? node) => node switch
    {
        JsonArray list => list.Select(x => x?.ToString()).OfType<string>().Where(s => s.Trim().Length > 0).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        _ => []
    };
}

// ---- Brevo ----------------------------------------------------------------------

/// <summary>
/// Brevo (formerly Sendinblue): transactional email and SMS, contacts, and notifications to you by
/// email or SMS. Emails carry an idempotency key derived from the call's, so a retried send within
/// Brevo's window isn't delivered twice.
/// </summary>
public sealed class BrevoPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin, INotificationChannelPlugin
{
    private const string Api = "https://api.brevo.com/v3";

    public PluginManifest Manifest { get; } = new()
    {
        Id = "brevo",
        Name = "Brevo (email & SMS)",
        Description = "Send transactional emails and SMS through Brevo, manage contacts, and get alerts by email or SMS.",
        Category = PluginCategory.Messaging,
        Settings =
        [
            new() { Key = "api_key", Label = "API key", Secret = true, Required = true, Placeholder = "xkeysib-…" },
            new() { Key = "sender_email", Label = "Sender email", Placeholder = "alerts@yourdomain.com", Description = "A sender verified in Brevo; needed for email." },
            new() { Key = "sender_name", Label = "Sender name", Placeholder = "Aktor" },
            new() { Key = "sms_sender", Label = "SMS sender", Placeholder = "Aktor (11 letters) or a number", Description = "Needed for SMS." },
            new() { Key = "notify_by", Label = "Send my alerts by", Options = ["email", "sms", "both"], DefaultValue = "email" },
            new() { Key = "notify_email", Label = "Your email (for alerts)" },
            new() { Key = "notify_phone", Label = "Your phone (for alerts)", Placeholder = "+15557654321" },
            new() { Key = "allow_contact_writes", Label = "Let agents add or update contacts", Options = ["false", "true"], DefaultValue = "false" }
        ],
        SetupHelp = "Create an API key under SMTP & API → API keys in Brevo. Verify the sender email (Senders, domains & dedicated IPs). " +
                    "SMS needs SMS credits on the account."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Api}/account", Auth(connection), null, cancellationToken);
        if (status != 200) return ConnectionCheck.Failure(Connector.Fail("Brevo", status, body, text).ErrorMessage!);
        var missing = new List<string>();
        if (connection.Setting("sender_email").Length == 0) missing.Add("a sender email (for email)");
        if (connection.Setting("sms_sender").Length == 0) missing.Add("an SMS sender (for SMS)");
        return ConnectionCheck.Success($"Connected to Brevo as {Connector.At(body, "email")}." + (missing.Count > 0 ? $" Add {string.Join(" and ", missing)} to send." : ""));
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        var tools = new List<ToolDefinition>
        {
            Connector.Tool("send_email", "Send an email. Write the body as plain text, or as HTML in html.", ToolSideEffects.NonIdempotent, """
                {
                  "type": "object",
                  "properties": {
                    "to": { "type": "string", "description": "One address, or several separated by commas. \"Ada <ada@example.com>\" works." },
                    "subject": { "type": "string" },
                    "text": { "type": "string", "description": "Plain-text body." },
                    "html": { "type": "string", "description": "HTML body (optional; text is used when it's missing)." },
                    "reply_to": { "type": "string" },
                    "cc": { "type": "string", "description": "Addresses separated by commas." },
                    "tags": { "type": "array", "items": { "type": "string" } }
                  },
                  "required": ["to", "subject"]
                }
                """),
            Connector.Tool("send_sms", "Send a transactional SMS to a phone number with its country code (e.g. +233201234567).", ToolSideEffects.NonIdempotent, """
                { "type": "object", "properties": { "to": { "type": "string" }, "text": { "type": "string" }, "tag": { "type": "string" } }, "required": ["to", "text"] }
                """),
            Connector.Tool("get_contact", "A contact by email (or Brevo id): attributes, lists, and whether they unsubscribed.", ToolSideEffects.ReadOnly, """
                { "type": "object", "properties": { "email": { "type": "string" } }, "required": ["email"] }
                """)
        };

        if (connection.Setting("allow_contact_writes", "false") == "true")
        {
            tools.Add(Connector.Tool("upsert_contact", "Add a contact, or update it if the email exists; optionally add it to lists.", ToolSideEffects.Idempotent, """
                {
                  "type": "object",
                  "properties": {
                    "email": { "type": "string" },
                    "attributes": { "type": "object", "description": "e.g. {\"FIRSTNAME\": \"Ada\", \"SMS\": \"+15551234567\"}" },
                    "list_ids": { "type": "array", "items": { "type": "integer" } }
                  },
                  "required": ["email"]
                }
                """));
        }

        return Task.FromResult<IReadOnlyList<ToolDefinition>>(tools);
    }

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        var args = Connector.Args(request);
        var ct = request.CancellationToken;
        switch (toolName)
        {
            case "send_email":
            {
                var to = Messaging.Recipients(args["to"]);
                if (to.Count == 0) throw new ArgumentException("'to' needs at least one address.");
                var text = Connector.Str(args, "text");
                var html = Connector.Str(args, "html");
                if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(html)) throw new ArgumentException("Give the email a text or html body.");
                var sent = await SendEmailAsync(connection, to, Messaging.Recipients(args["cc"]), Connector.Required(args, "subject"), text, html,
                    Connector.Str(args, "reply_to"), args["tags"] as JsonArray, request.IdempotencyKey, ct);
                return sent.Delivered ? ToolExecutionResult.Ok("""{"sent":true}""") : ToolExecutionResult.Fail(sent.Error!);
            }

            case "send_sms":
            {
                var sent = await SendSmsAsync(connection, Connector.Required(args, "to"), Connector.Required(args, "text"), Connector.Str(args, "tag"), ct);
                return sent.Delivered ? ToolExecutionResult.Ok("""{"sent":true}""") : ToolExecutionResult.Fail(sent.Error!);
            }

            case "get_contact":
            {
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get,
                    $"{Api}/contacts/{Uri.EscapeDataString(Connector.Required(args, "email"))}", Auth(connection), null, ct);
                if (status == 404) return Connector.Ok(new JsonObject { ["found"] = false });
                if (status != 200) return Connector.Fail("Brevo", status, body, text);
                return Connector.Ok(new JsonObject
                {
                    ["found"] = true,
                    ["id"] = body?["id"]?.DeepClone(),
                    ["email"] = Connector.At(body, "email"),
                    ["attributes"] = body?["attributes"]?.DeepClone(),
                    ["list_ids"] = body?["listIds"]?.DeepClone(),
                    ["email_blacklisted"] = body?["emailBlacklisted"]?.DeepClone(),
                    ["sms_blacklisted"] = body?["smsBlacklisted"]?.DeepClone()
                });
            }

            case "upsert_contact" when connection.Setting("allow_contact_writes", "false") == "true":
            {
                var payload = new JsonObject { ["email"] = Connector.Required(args, "email"), ["updateEnabled"] = true };
                if (args["attributes"] is JsonObject attributes) payload["attributes"] = attributes.DeepClone();
                if (args["list_ids"] is JsonArray lists) payload["listIds"] = lists.DeepClone();
                var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Post, $"{Api}/contacts", Auth(connection), payload, ct);
                // 201: created; 204: an existing contact was updated.
                return status is 201 or 204
                    ? Connector.Ok(new JsonObject { ["saved"] = true, ["created"] = status == 201, ["id"] = body?["id"]?.DeepClone() })
                    : Connector.Fail("Brevo", status, body, text);
            }

            default:
                return ToolExecutionResult.Fail($"Unknown or disabled tool '{toolName}'.");
        }
    });

    public async Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification n, CancellationToken cancellationToken)
    {
        var by = connection.Setting("notify_by", "email");
        DeliveryResult? result = null;
        if (by is "email" or "both")
        {
            var to = connection.Setting("notify_email");
            if (to.Length == 0) return DeliveryResult.Failed("No 'your email' is set on this connection.", retryable: false);
            var body = Messaging.Body(n);
            result = await SendEmailAsync(connection, [to], [], Messaging.Subject(n), body, Messaging.Html(body), null, null, n.DeliveryId, cancellationToken);
            if (!result.Delivered) return result;
        }

        if (by is "sms" or "both")
        {
            var phone = connection.Setting("notify_phone");
            if (phone.Length == 0) return DeliveryResult.Failed("No 'your phone' is set on this connection.", retryable: false);
            // Brevo can't deduplicate SMS, and the email (if any) already went: don't retry into a second email.
            var sms = await SendSmsAsync(connection, phone, MessagingText.Truncate($"[{n.WorkspaceName}] {n.Text}", 600), "aktor-alert", cancellationToken);
            result = by == "both" && !sms.Delivered ? DeliveryResult.Failed(sms.Error!, retryable: false) : sms;
        }

        return result ?? DeliveryResult.Failed($"Unknown 'send my alerts by' value '{by}'.", retryable: false);
    }

    private async Task<DeliveryResult> SendEmailAsync(PluginConnection c, List<string> to, List<string> cc, string subject, string? text, string? html,
        string? replyTo, JsonArray? tags, string idempotencyKey, CancellationToken ct)
    {
        var senderEmail = c.Setting("sender_email");
        if (senderEmail.Length == 0) return DeliveryResult.Failed("Set a sender email on the Brevo connection to send email.", retryable: false);
        JsonObject Contact(string address)
        {
            var (email, name) = Messaging.Address(address);
            return name is null ? new JsonObject { ["email"] = email } : new JsonObject { ["email"] = email, ["name"] = name };
        }

        var payload = new JsonObject
        {
            ["sender"] = new JsonObject { ["email"] = senderEmail, ["name"] = c.Setting("sender_name", "Aktor") },
            ["to"] = new JsonArray([.. to.Select(a => (JsonNode?)Contact(a))]),
            ["subject"] = subject,
            ["htmlContent"] = string.IsNullOrWhiteSpace(html) ? Messaging.Html(text ?? "") : html
        };
        if (!string.IsNullOrWhiteSpace(text)) payload["textContent"] = text;
        if (cc.Count > 0) payload["cc"] = new JsonArray([.. cc.Select(a => (JsonNode?)Contact(a))]);
        if (!string.IsNullOrWhiteSpace(replyTo)) payload["replyTo"] = Contact(replyTo);
        if (tags is { Count: > 0 }) payload["tags"] = tags.DeepClone();
        // Brevo drops a request it has seen with this key (within its window).
        if (!string.IsNullOrEmpty(idempotencyKey)) payload["headers"] = new JsonObject { ["idempotencyKey"] = Messaging.Uuid(idempotencyKey) };

        var (status, body, responseText) = await Connector.SendAsync(Http, HttpMethod.Post, $"{Api}/smtp/email", Auth(c), payload, ct);
        if (status is 200 or 201 or 202) return DeliveryResult.Ok();
        // Seen this key already: that send went through.
        if (Connector.At(body, "code") == "duplicate_parameter") return DeliveryResult.Ok();
        return Messaging.Failure("Brevo", status, body, responseText);
    }

    private async Task<DeliveryResult> SendSmsAsync(PluginConnection c, string to, string text, string? tag, CancellationToken ct)
    {
        var sender = c.Setting("sms_sender");
        if (sender.Length == 0) return DeliveryResult.Failed("Set an SMS sender on the Brevo connection to send SMS.", retryable: false);
        var payload = new JsonObject
        {
            ["sender"] = sender,
            ["recipient"] = to.Trim().TrimStart('+'),
            ["content"] = text,
            ["type"] = "transactional"
        };
        if (!string.IsNullOrWhiteSpace(tag)) payload["tag"] = tag;
        var (status, body, responseText) = await Connector.SendAsync(Http, HttpMethod.Post, $"{Api}/transactionalSMS/send", Auth(c), payload, ct);
        return status is 200 or 201 or 202 ? DeliveryResult.Ok() : Messaging.Failure("Brevo", status, body, responseText);
    }

    private static Action<HttpRequestMessage> Auth(PluginConnection c)
    {
        var key = c.Secret("api_key");
        return r =>
        {
            r.Headers.TryAddWithoutValidation("api-key", key);
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        };
    }

    private HttpClient Http => httpClientFactory.CreateClient("integrations");
}

// ---- SendGrid -------------------------------------------------------------------

/// <summary>SendGrid (Twilio): transactional email, and notifications to you by email.</summary>
public sealed class SendGridPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin, INotificationChannelPlugin
{
    private const string Api = "https://api.sendgrid.com/v3";

    public PluginManifest Manifest { get; } = new()
    {
        Id = "sendgrid",
        Name = "SendGrid (email)",
        Description = "Send transactional emails through SendGrid, and get alerts by email.",
        Category = PluginCategory.Messaging,
        Settings =
        [
            new() { Key = "api_key", Label = "API key", Secret = true, Required = true, Placeholder = "SG.…" },
            new() { Key = "from_email", Label = "From address", Required = true, Placeholder = "alerts@yourdomain.com", Description = "A verified sender or an address on an authenticated domain." },
            new() { Key = "from_name", Label = "From name", Placeholder = "Aktor" },
            new() { Key = "api_base", Label = "Region", Options = ["global", "eu"], DefaultValue = "global", Description = "eu for an EU-pinned SendGrid account." },
            new() { Key = "notify_to", Label = "Your email (for alerts)" }
        ],
        SetupHelp = "Create an API key with Mail Send permission under Settings → API Keys, and verify the from address " +
                    "(Settings → Sender Authentication)."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) => Connector.Check(async () =>
    {
        var (status, body, text) = await Connector.SendAsync(Http, HttpMethod.Get, $"{Base(connection)}/scopes", Auth(connection), null, cancellationToken);
        if (status != 200) return ConnectionCheck.Failure(Connector.Fail("SendGrid", status, body, text).ErrorMessage!);
        var scopes = (body?["scopes"] as JsonArray)?.Select(s => s?.ToString()).ToList() ?? [];
        return scopes.Contains("mail.send")
            ? ConnectionCheck.Success("Connected to SendGrid; the key can send mail.")
            : ConnectionCheck.Failure("The API key works but can't send mail: give it the Mail Send permission.");
    });

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            Connector.Tool("send_email", "Send an email. Write the body as plain text, or as HTML in html.", ToolSideEffects.NonIdempotent, """
                {
                  "type": "object",
                  "properties": {
                    "to": { "type": "string", "description": "One address, or several separated by commas. \"Ada <ada@example.com>\" works." },
                    "subject": { "type": "string" },
                    "text": { "type": "string" },
                    "html": { "type": "string" },
                    "reply_to": { "type": "string" },
                    "cc": { "type": "string", "description": "Addresses separated by commas." },
                    "categories": { "type": "array", "items": { "type": "string" } }
                  },
                  "required": ["to", "subject"]
                }
                """)
        ]);

    public Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request) => Connector.Guard(async () =>
    {
        if (toolName != "send_email") return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        var args = Connector.Args(request);
        var to = Messaging.Recipients(args["to"]);
        if (to.Count == 0) throw new ArgumentException("'to' needs at least one address.");
        var text = Connector.Str(args, "text");
        var html = Connector.Str(args, "html");
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(html)) throw new ArgumentException("Give the email a text or html body.");
        var sent = await SendAsync(connection, to, Messaging.Recipients(args["cc"]), Connector.Required(args, "subject"), text, html,
            Connector.Str(args, "reply_to"), args["categories"] as JsonArray, request.IdempotencyKey, request.CancellationToken);
        return sent.Delivered ? ToolExecutionResult.Ok("""{"sent":true}""") : ToolExecutionResult.Fail(sent.Error!);
    });

    public Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification n, CancellationToken cancellationToken)
    {
        var to = connection.Setting("notify_to");
        if (to.Length == 0) return Task.FromResult(DeliveryResult.Failed("No 'your email' is set on this connection.", retryable: false));
        var body = Messaging.Body(n);
        return SendAsync(connection, [to], [], Messaging.Subject(n), body, Messaging.Html(body), null, null, n.DeliveryId, cancellationToken);
    }

    private async Task<DeliveryResult> SendAsync(PluginConnection c, List<string> to, List<string> cc, string subject, string? text, string? html,
        string? replyTo, JsonArray? categories, string key, CancellationToken ct)
    {
        static JsonObject Address(string value)
        {
            var (email, name) = Messaging.Address(value);
            return name is null ? new JsonObject { ["email"] = email } : new JsonObject { ["email"] = email, ["name"] = name };
        }

        var personalization = new JsonObject { ["to"] = new JsonArray([.. to.Select(a => (JsonNode?)Address(a))]) };
        if (cc.Count > 0) personalization["cc"] = new JsonArray([.. cc.Select(a => (JsonNode?)Address(a))]);
        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(text)) content.Add(new JsonObject { ["type"] = "text/plain", ["value"] = text });
        content.Add(new JsonObject { ["type"] = "text/html", ["value"] = string.IsNullOrWhiteSpace(html) ? Messaging.Html(text ?? "") : html });

        var from = new JsonObject { ["email"] = c.Setting("from_email") };
        if (c.Setting("from_name").Length > 0) from["name"] = c.Setting("from_name");
        var payload = new JsonObject
        {
            ["personalizations"] = new JsonArray(personalization),
            ["from"] = from,
            ["subject"] = subject,
            ["content"] = content
        };
        if (!string.IsNullOrWhiteSpace(replyTo)) payload["reply_to"] = Address(replyTo);
        if (categories is { Count: > 0 }) payload["categories"] = categories.DeepClone();
        // SendGrid can't deduplicate; the key at least ties the message to the call in its activity feed.
        if (!string.IsNullOrEmpty(key)) payload["custom_args"] = new JsonObject { ["aktor_key"] = MessagingText.Truncate(key, 200) };

        var (status, body, responseText) = await Connector.SendAsync(Http, HttpMethod.Post, $"{Base(c)}/mail/send", Auth(c), payload, ct);
        return status is 200 or 202 ? DeliveryResult.Ok() : Messaging.Failure("SendGrid", status, body, responseText);
    }

    private static string Base(PluginConnection c) => c.Setting("api_base", "global") == "eu" ? "https://api.eu.sendgrid.com/v3" : Api;

    private static Action<HttpRequestMessage> Auth(PluginConnection c)
    {
        var key = c.Secret("api_key");
        return r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private HttpClient Http => httpClientFactory.CreateClient("integrations");
}
