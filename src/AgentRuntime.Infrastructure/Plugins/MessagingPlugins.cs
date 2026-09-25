using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Plugins;
using AgentRuntime.Tools;

namespace AgentRuntime.Infrastructure.Plugins;

internal static class MessagingText
{
    public static string Notification(OutboundNotification n, int max)
    {
        var prefix = n.Urgency == "urgent" ? "🚨 " : n.Urgency == "warning" ? "⚠️ " : string.Empty;
        var text = $"{prefix}[{n.WorkspaceName}] {n.AuthorName}: {n.Text}";
        return text.Length > max ? text[..(max - 1)] + "…" : text;
    }

    public static string Arg(ToolExecutionRequest request, string name)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
        return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    }

    public static DeliveryResult FromStatus(HttpStatusCode status, string body) =>
        (int)status is >= 200 and < 300 ? DeliveryResult.Ok()
        // 4xx other than rate limiting won't be fixed by retrying (bad token, bad number).
        : DeliveryResult.Failed($"{(int)status}: {Truncate(body, 300)}", retryable: (int)status is 408 or 429 or >= 500);

    public static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}

// ---- Slack ----------------------------------------------------------------------

/// <summary>Posts to a Slack channel through an incoming webhook.</summary>
public sealed class SlackPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin, INotificationChannelPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "slack",
        Name = "Slack",
        Description = "Send workspace notifications to a Slack channel, and let agents post there.",
        Category = PluginCategory.Messaging,
        Settings = [new() { Key = "webhook_url", Label = "Incoming webhook URL", Secret = true, Required = true, Placeholder = "https://hooks.slack.com/services/…" }],
        SetupHelp = "In Slack: Apps → Incoming Webhooks → Add to a channel, then paste the webhook URL."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(Uri.TryCreate(connection.Secret("webhook_url"), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.EndsWith("slack.com")
            ? ConnectionCheck.Success("Slack webhook saved.")
            : ConnectionCheck.Failure("That doesn't look like a Slack incoming webhook URL (https://hooks.slack.com/…)."));

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            new()
            {
                Name = "post_message",
                Description = "Post a message to the connected Slack channel (seen by everyone in it).",
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """{ "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] }"""
            }
        ]);

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        if (toolName != "post_message") return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        var result = await PostAsync(connection, MessagingText.Arg(request, "text"), request.CancellationToken);
        return result.Delivered ? ToolExecutionResult.Ok("""{"posted":true}""") : ToolExecutionResult.Fail(result.Error ?? "Slack rejected the message.");
    }

    public Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification notification, CancellationToken cancellationToken) =>
        PostAsync(connection, MessagingText.Notification(notification, 3000), cancellationToken);

    private async Task<DeliveryResult> PostAsync(PluginConnection connection, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return DeliveryResult.Failed("text is empty", retryable: false);
        using var response = await httpClientFactory.CreateClient("integrations").PostAsJsonAsync(connection.Secret("webhook_url"), new { text }, ct);
        return MessagingText.FromStatus(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }
}

// ---- Twilio SMS -----------------------------------------------------------------

/// <summary>SMS via Twilio: notifications to the owner's phone, an SMS tool for agents, and
/// replies from allowed numbers become commands. Inbound requests are checked against Twilio's
/// X-Twilio-Signature when the server's public URL is configured.</summary>
public sealed class TwilioSmsPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin, INotificationChannelPlugin, IInboundChannelPlugin
{
    public const string ApiBase = "https://api.twilio.com/2010-04-01";
    private const string EmptyTwiml = """<?xml version="1.0" encoding="UTF-8"?><Response></Response>""";

    public PluginManifest Manifest { get; } = new()
    {
        Id = "twilio-sms",
        Name = "SMS (Twilio)",
        Description = "Text alerts to your phone, let agents send SMS, and reply by SMS to give your agents instructions.",
        Category = PluginCategory.Messaging,
        Settings =
        [
            new() { Key = "account_sid", Label = "Account SID", Required = true, Placeholder = "AC…" },
            new() { Key = "auth_token", Label = "Auth token", Secret = true, Required = true },
            new() { Key = "from_number", Label = "Twilio phone number", Required = true, Placeholder = "+15551234567" },
            new() { Key = "notify_to", Label = "Your phone number (for alerts)", Placeholder = "+15557654321" }
        ],
        SetupHelp = "Find the SID and auth token in the Twilio console. To command agents by SMS, set the number's " +
                    "'A message comes in' webhook to this connection's inbound URL and add your number to allowed senders."
    };

    public async Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/Accounts/{connection.Setting("account_sid")}.json");
        request.Headers.Authorization = Basic(connection);
        using var response = await httpClientFactory.CreateClient("integrations").SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? ConnectionCheck.Success("Twilio account verified.")
            : ConnectionCheck.Failure($"Twilio rejected the credentials ({(int)response.StatusCode}).");
    }

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            new()
            {
                Name = "send_sms",
                Description = "Send an SMS to a phone number (E.164, e.g. +15551234567). For alerting the workspace owner, use notify_user instead.",
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """{ "type": "object", "properties": { "to": { "type": "string" }, "body": { "type": "string" } }, "required": ["to", "body"] }"""
            }
        ]);

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        if (toolName != "send_sms") return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        var result = await SendAsync(connection, MessagingText.Arg(request, "to"), MessagingText.Arg(request, "body"), request.CancellationToken);
        return result.Delivered ? ToolExecutionResult.Ok("""{"sent":true}""") : ToolExecutionResult.Fail(result.Error ?? "Twilio rejected the message.");
    }

    public Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification notification, CancellationToken cancellationToken)
    {
        var to = connection.Setting("notify_to");
        return string.IsNullOrWhiteSpace(to)
            ? Task.FromResult(DeliveryResult.Failed("No 'your phone number' is set on this connection.", retryable: false))
            : SendAsync(connection, to, MessagingText.Notification(notification, 1500), cancellationToken);
    }

    public Task<InboundResult> ParseInboundAsync(PluginConnection connection, InboundRequest request, CancellationToken cancellationToken)
    {
        var form = ParseForm(request.Body);

        // With a known public URL, require Twilio's signature. Without one we can't compute it, and
        // the secret in the inbound URL is the only protection (documented).
        if (request.PublicUrl is not null)
        {
            request.Headers.TryGetValue("X-Twilio-Signature", out var signature);
            if (signature is null || !SignatureMatches(connection.Secret("auth_token"), request.PublicUrl, form, signature))
            {
                return Task.FromResult(InboundResult.Reject());
            }
        }

        if (!form.TryGetValue("Body", out var body) || string.IsNullOrWhiteSpace(body))
        {
            return Task.FromResult(InboundResult.Ignore(EmptyTwiml, "text/xml"));
        }

        return Task.FromResult(new InboundResult
        {
            IsMessage = true,
            SenderId = form.GetValueOrDefault("From"),
            Text = body.Trim(),
            MessageId = form.GetValueOrDefault("MessageSid"),
            ResponseBody = EmptyTwiml,
            ResponseContentType = "text/xml"
        });
    }

    /// <summary>Twilio's scheme: base64(HMAC-SHA1(authToken, url + each POST param key+value, sorted by key)).</summary>
    public static bool SignatureMatches(string authToken, string url, IReadOnlyDictionary<string, string> form, string signature)
    {
        var data = new StringBuilder(url);
        foreach (var (key, value) in form.OrderBy(kv => kv.Key, StringComparer.Ordinal)) data.Append(key).Append(value);
        var expected = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(data.ToString())));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    public static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .GroupBy(kv => WebUtility.UrlDecode(kv[0]))
            .ToDictionary(g => g.Key, g => WebUtility.UrlDecode(g.First().ElementAtOrDefault(1) ?? string.Empty));

    private async Task<DeliveryResult> SendAsync(PluginConnection connection, string to, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(body)) return DeliveryResult.Failed("to and body are required", retryable: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/Accounts/{connection.Setting("account_sid")}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = to.Trim(),
                ["From"] = connection.Setting("from_number"),
                ["Body"] = body
            })
        };
        request.Headers.Authorization = Basic(connection);
        using var response = await httpClientFactory.CreateClient("integrations").SendAsync(request, ct);
        return MessagingText.FromStatus(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static AuthenticationHeaderValue Basic(PluginConnection c) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{c.Setting("account_sid")}:{c.Secret("auth_token")}")));
}

// ---- Email (SMTP) ---------------------------------------------------------------

/// <summary>Email over SMTP (STARTTLS on 587, or no TLS for local relays).</summary>
public sealed class EmailSmtpPlugin : IToolProviderPlugin, INotificationChannelPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "email-smtp",
        Name = "Email (SMTP)",
        Description = "Email notifications to you, and let agents send email (e.g. to a supplier).",
        Category = PluginCategory.Messaging,
        Settings =
        [
            new() { Key = "host", Label = "SMTP host", Required = true, Placeholder = "smtp.gmail.com" },
            new() { Key = "port", Label = "Port", DefaultValue = "587" },
            new() { Key = "tls", Label = "TLS", Options = ["starttls", "none"], DefaultValue = "starttls" },
            new() { Key = "username", Label = "Username" },
            new() { Key = "password", Label = "Password / app password", Secret = true },
            new() { Key = "from_address", Label = "From address", Required = true },
            new() { Key = "notify_to", Label = "Your email (for alerts)" }
        ],
        SetupHelp = "For Gmail use smtp.gmail.com:587 with STARTTLS and an app password."
    };

    public Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            _ = new MailAddress(connection.Setting("from_address"));
            return Task.FromResult(ConnectionCheck.Success("Email settings saved (sending is checked on first use)."));
        }
        catch (FormatException)
        {
            return Task.FromResult(ConnectionCheck.Failure("The from address isn't a valid email address."));
        }
    }

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            new()
            {
                Name = "send_email",
                Description = "Send an email. For alerting the workspace owner, use notify_user instead.",
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """
                { "type": "object", "properties": { "to": { "type": "string" }, "subject": { "type": "string" }, "body": { "type": "string" } }, "required": ["to", "subject", "body"] }
                """
            }
        ]);

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        if (toolName != "send_email") return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        var result = await SendAsync(connection, MessagingText.Arg(request, "to"), MessagingText.Arg(request, "subject"),
            MessagingText.Arg(request, "body"), request.IdempotencyKey, request.CancellationToken);
        return result.Delivered ? ToolExecutionResult.Ok("""{"sent":true}""") : ToolExecutionResult.Fail(result.Error ?? "The mail server rejected the message.");
    }

    public Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification n, CancellationToken cancellationToken)
    {
        var to = connection.Setting("notify_to");
        if (string.IsNullOrWhiteSpace(to)) return Task.FromResult(DeliveryResult.Failed("No 'your email' is set on this connection.", retryable: false));
        var firstLine = n.Text.Split('\n')[0];
        var subject = $"[{n.WorkspaceName}] {(n.Urgency == "urgent" ? "URGENT: " : string.Empty)}{MessagingText.Truncate(firstLine, 70)}";
        return SendAsync(connection, to, subject, $"{n.AuthorName} says:\n\n{n.Text}", n.DeliveryId, cancellationToken);
    }

    private static async Task<DeliveryResult> SendAsync(PluginConnection c, string to, string subject, string body, string messageKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to)) return DeliveryResult.Failed("to is required", retryable: false);
        try
        {
            using var message = new MailMessage(c.Setting("from_address"), to.Trim(), subject, body);
            // A stable Message-ID lets receiving servers and clients spot a retried duplicate.
            if (!string.IsNullOrEmpty(messageKey))
            {
                var domain = new MailAddress(c.Setting("from_address")).Host;
                message.Headers["Message-ID"] = $"<{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageKey)))[..32].ToLowerInvariant()}@{domain}>";
            }

            using var smtp = new SmtpClient(c.Setting("host"), int.TryParse(c.Setting("port", "587"), out var port) ? port : 587)
            {
                EnableSsl = c.Setting("tls", "starttls") == "starttls",
                DeliveryMethod = SmtpDeliveryMethod.Network
            };
            if (!string.IsNullOrWhiteSpace(c.Setting("username")))
            {
                smtp.Credentials = new NetworkCredential(c.Setting("username"), c.Secrets.GetValueOrDefault("password"));
            }

            await smtp.SendMailAsync(message, ct);
            return DeliveryResult.Ok();
        }
        catch (SmtpFailedRecipientException ex)
        {
            return DeliveryResult.Failed(ex.Message, retryable: false);
        }
        catch (SmtpException ex)
        {
            return DeliveryResult.Failed(ex.Message, retryable: ex.StatusCode is not (SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MustIssueStartTlsFirst));
        }
        catch (FormatException ex)
        {
            return DeliveryResult.Failed(ex.Message, retryable: false);
        }
    }
}

// ---- Telegram -------------------------------------------------------------------

/// <summary>A Telegram bot: notifications to your chat, a messaging tool, and messages you send
/// the bot become commands. Inbound updates must carry the per-connection secret token that the
/// plugin registers with Telegram (setWebhook secret_token).</summary>
public sealed class TelegramPlugin(IHttpClientFactory httpClientFactory) : IToolProviderPlugin, INotificationChannelPlugin, IInboundChannelPlugin
{
    public const string ApiBase = "https://api.telegram.org";

    public PluginManifest Manifest { get; } = new()
    {
        Id = "telegram",
        Name = "Telegram",
        Description = "Alerts in Telegram, and message the bot to give your agents instructions.",
        Category = PluginCategory.Messaging,
        Settings =
        [
            new() { Key = "bot_token", Label = "Bot token", Secret = true, Required = true, Placeholder = "123456:ABC…" },
            new() { Key = "notify_chat_id", Label = "Your chat id (for alerts)", Placeholder = "123456789" }
        ],
        SetupHelp = "Create a bot with @BotFather and paste its token. Message the bot, then find your chat id (e.g. via " +
                    "@userinfobot) and add it as notify chat id and as an allowed sender. With a public server URL configured, " +
                    "the webhook is registered automatically."
    };

    public async Task<ConnectionCheck> ValidateAsync(PluginConnection connection, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("integrations");
        using var me = await client.GetAsync(Method(connection, "getMe"), cancellationToken);
        if (!me.IsSuccessStatusCode) return ConnectionCheck.Failure($"Telegram rejected the bot token ({(int)me.StatusCode}).");

        if (connection.InboundUrl is null)
        {
            return ConnectionCheck.Success("Bot verified. Set Integrations:PublicBaseUrl to receive messages from Telegram.");
        }

        using var hook = await client.PostAsJsonAsync(Method(connection, "setWebhook"), new
        {
            url = connection.InboundUrl,
            secret_token = connection.InboundSecret,
            allowed_updates = new[] { "message" }
        }, cancellationToken);
        return hook.IsSuccessStatusCode
            ? ConnectionCheck.Success("Bot verified and webhook registered.")
            : ConnectionCheck.Failure($"Bot verified, but registering the webhook failed ({(int)hook.StatusCode}).");
    }

    public Task<IReadOnlyList<ToolDefinition>> ListToolsAsync(PluginConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ToolDefinition>>([
            new()
            {
                Name = "send_message",
                Description = "Send a Telegram message to a chat id. For alerting the workspace owner, use notify_user instead.",
                SideEffects = ToolSideEffects.NonIdempotent,
                JsonSchema = """{ "type": "object", "properties": { "chat_id": { "type": "string" }, "text": { "type": "string" } }, "required": ["chat_id", "text"] }"""
            }
        ]);

    public async Task<ToolExecutionResult> ExecuteToolAsync(PluginConnection connection, string toolName, ToolExecutionRequest request)
    {
        if (toolName != "send_message") return ToolExecutionResult.Fail($"Unknown tool '{toolName}'.");
        var result = await SendAsync(connection, MessagingText.Arg(request, "chat_id"), MessagingText.Arg(request, "text"), request.CancellationToken);
        return result.Delivered ? ToolExecutionResult.Ok("""{"sent":true}""") : ToolExecutionResult.Fail(result.Error ?? "Telegram rejected the message.");
    }

    public Task<DeliveryResult> SendNotificationAsync(PluginConnection connection, OutboundNotification notification, CancellationToken cancellationToken)
    {
        var chat = connection.Setting("notify_chat_id");
        return string.IsNullOrWhiteSpace(chat)
            ? Task.FromResult(DeliveryResult.Failed("No notify chat id is set on this connection.", retryable: false))
            : SendAsync(connection, chat, MessagingText.Notification(notification, 4000), cancellationToken);
    }

    public Task<InboundResult> ParseInboundAsync(PluginConnection connection, InboundRequest request, CancellationToken cancellationToken)
    {
        request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var token);
        if (token is null || connection.InboundSecret is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(connection.InboundSecret)))
        {
            return Task.FromResult(InboundResult.Reject());
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var text) ||
            message.TryGetProperty("from", out var from) && from.TryGetProperty("is_bot", out var bot) && bot.GetBoolean())
        {
            return Task.FromResult(InboundResult.Ignore());
        }

        return Task.FromResult(new InboundResult
        {
            IsMessage = true,
            SenderId = message.GetProperty("chat").GetProperty("id").GetRawText(),
            SenderName = message.TryGetProperty("from", out var f) && f.TryGetProperty("first_name", out var n) ? n.GetString() : null,
            Text = text.GetString(),
            MessageId = root.TryGetProperty("update_id", out var id) ? id.GetRawText() : null
        });
    }

    private async Task<DeliveryResult> SendAsync(PluginConnection connection, string chatId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(text)) return DeliveryResult.Failed("chat_id and text are required", retryable: false);
        using var response = await httpClientFactory.CreateClient("integrations")
            .PostAsJsonAsync(Method(connection, "sendMessage"), new { chat_id = chatId.Trim(), text }, ct);
        return MessagingText.FromStatus(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static string Method(PluginConnection c, string method) => $"{ApiBase}/bot{c.Secret("bot_token")}/{method}";
}
