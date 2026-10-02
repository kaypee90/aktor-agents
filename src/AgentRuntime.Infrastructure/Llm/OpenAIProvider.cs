using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// <see cref="ILLMProvider"/> implementation against the OpenAI Chat Completions API, proving the
/// abstraction in CLAUDE.md section 2 supports more than one provider without touching the Agent
/// runtime.
///
/// <para>Two model generations share this one endpoint but not one request shape. The
/// reasoning-era models (o-series, GPT-5 and later) renamed the output cap to
/// <c>max_completion_tokens</c> and reject the sampling parameters outright; the older models
/// still take <c>max_tokens</c> and a temperature. Sending the wrong one is a hard 400 rather
/// than a warning, so <see cref="OpenAiCapabilities"/> works out which shape a model speaks —
/// from its id, and from the API's own error when the id isn't conclusive.</para>
/// </summary>
public sealed class OpenAIProvider(HttpClient httpClient, IOptions<LlmOptions> options) : ILLMProvider
{
    /// <summary>How many times one call may be re-shaped after the API rejects its parameters.
    /// There are only two knobs that differ between generations, so two corrections always
    /// suffice; the bound is there so a malformed error can never spin.</summary>
    private const int MaxShapeAttempts = 2;

    public string ProviderName => "OpenAI";

    private readonly LlmOptions _options = options.Value;

    /// <summary>What each model speaks, learned from its id and corrected by the API. Keyed by
    /// model id, so a cheap <c>LLM_FAST_MODEL</c> and an expensive main model can speak different
    /// shapes at once, and a model released after this ships costs one rejected request rather
    /// than failing every call.</summary>
    private readonly ConcurrentDictionary<string, OpenAiCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public async Task<LlmCompletionResponse> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? throw new InvalidOperationException("OpenAI API key not configured (Llm:ApiKey / LLM_API_KEY).")
            : _options.ApiKey;

        var model = request.Model ?? _options.Model;
        var capabilities = _capabilities.GetOrAdd(model, OpenAiCapabilities.Detect);

        for (var attempt = 0; ; attempt++)
        {
            // A model that can't do tools on Chat Completions goes to Responses for every call,
            // not just the ones using tools: switching per-request would let one call's rejection
            // and another's knowledge of the fix disagree.
            var path = capabilities.RequiresResponsesApi ? "v1/responses" : "v1/chat/completions";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(capabilities.RequiresResponsesApi
                    ? BuildResponsesBody(request, model, capabilities)
                    : BuildBody(request, model, capabilities))
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return capabilities.RequiresResponsesApi
                    ? ParseResponsesResponse(responseBody)
                    : ParseResponse(responseBody);
            }

            // A 400 is free and says exactly which parameter was wrong: take the correction and
            // retry rather than failing a task over a field name.
            if (attempt < MaxShapeAttempts)
            {
                var adjusted = OpenAiCapabilities.Downgrade(capabilities, responseBody);
                if (adjusted is not null && adjusted != capabilities)
                {
                    _capabilities[model] = adjusted;
                    capabilities = adjusted;
                    continue;
                }
            }

            throw new InvalidOperationException($"OpenAI API error {(int)response.StatusCode}: {responseBody}");
        }
    }

    private JsonObject BuildBody(LlmCompletionRequest request, string model, OpenAiCapabilities capabilities)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request.Messages)
        };

        if (capabilities.UseMaxCompletionTokens)
        {
            // Reasoning tokens are drawn from the same allowance as the answer, so a cap sized
            // for a non-reasoning model (1024 for routine calls, 4096 for real work) can be
            // spent before a single visible token is written. Floor it so the model can finish
            // thinking. A cap is a ceiling, not a reservation: the call still costs only what it
            // uses, and the agent's own budget still bounds the total.
            body["max_completion_tokens"] = Math.Max(request.MaxTokens, _options.ReasoningMinOutputTokens);
        }
        else
        {
            body["max_tokens"] = request.MaxTokens;
        }

        // Rejected outright by the reasoning-era models, so sending one is a 400, not a warning.
        if (capabilities.SupportsSampling)
        {
            body["temperature"] = request.Temperature;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.JsonSchema)
                }
            }).ToArray());
            body["tool_choice"] = "auto";
        }

        return body;
    }

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var obj = new JsonObject
            {
                ["role"] = message.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    ChatRole.Tool => "tool",
                    _ => "user"
                }
            };

            if (message.Role == ChatRole.Tool)
            {
                obj["tool_call_id"] = message.ToolCallId;
                obj["content"] = message.Content ?? string.Empty;
            }
            else if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 })
            {
                obj["content"] = message.Content;
                obj["tool_calls"] = new JsonArray(message.ToolCalls.Select(tc => (JsonNode)new JsonObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.ArgumentsJson }
                }).ToArray());
            }
            else
            {
                obj["content"] = message.Content ?? string.Empty;
            }

            array.Add(obj);
        }

        return array;
    }

    /// <summary>
    /// The /v1/responses body. Same conversation and same tools as the Chat Completions body,
    /// renamed: the system prompt becomes <c>instructions</c>, the output cap becomes
    /// <c>max_output_tokens</c>, tools lose their <c>function</c> wrapper, and the message list
    /// carries function calls and their results as typed items rather than special roles.
    /// </summary>
    private JsonObject BuildResponsesBody(LlmCompletionRequest request, string model, OpenAiCapabilities capabilities)
    {
        var system = request.Messages
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c));

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = BuildResponsesInput(request.Messages),
            // Agent conversations are state the runtime owns and re-sends every turn; keeping
            // them on OpenAI's servers would retain prompts the runtime never intends to store.
            ["store"] = false
        };

        if (system.Any())
        {
            body["instructions"] = string.Join("\n\n", system);
        }

        body["max_output_tokens"] = Math.Max(request.MaxTokens, _options.ReasoningMinOutputTokens);

        if (capabilities.SupportsSampling)
        {
            body["temperature"] = request.Temperature;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.JsonSchema)
            }).ToArray());
            body["tool_choice"] = "auto";
        }

        return body;
    }

    private static JsonArray BuildResponsesInput(IReadOnlyList<ChatMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            // The system prompt rides in "instructions" instead, and repeated here it would
            // be counted twice against the context.
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            switch (message.Role)
            {
                case ChatRole.Tool:
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.ToolCallId,
                        ["output"] = message.Content ?? string.Empty
                    });
                    break;

                case ChatRole.Assistant when message.ToolCalls is { Count: > 0 }:
                    // Text alongside tool calls is a separate item, and is optional.
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        input.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = message.Content
                        });
                    }

                    foreach (var toolCall in message.ToolCalls)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = toolCall.Id,
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.ArgumentsJson
                        });
                    }

                    break;

                default:
                    input.Add(new JsonObject
                    {
                        ["role"] = message.Role == ChatRole.Assistant ? "assistant" : "user",
                        ["content"] = message.Content ?? string.Empty
                    });
                    break;
            }
        }

        return input;
    }

    /// <summary>
    /// Reads a /v1/responses result. The answer arrives as a list of typed output items rather
    /// than one message object, so text and tool calls are collected from the same walk and the
    /// token counts come from differently-named fields.
    /// </summary>
    internal static LlmCompletionResponse ParseResponsesResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        string? content = null;
        var toolCalls = new List<ToolCall>();
        var incomplete = false;

        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                switch (item.TryGetProperty("type", out var type) ? type.GetString() : null)
                {
                    case "function_call":
                        toolCalls.Add(new ToolCall
                        {
                            // call_id is what the matching function_call_output must reference.
                            Id = item.GetProperty("call_id").GetString()!,
                            Name = item.GetProperty("name").GetString()!,
                            ArgumentsJson = item.GetProperty("arguments").GetString() ?? "{}"
                        });
                        break;

                    case "message":
                        if (item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("type", out var partType) &&
                                    partType.GetString() == "output_text" &&
                                    part.TryGetProperty("text", out var text))
                                {
                                    content = content is null ? text.GetString() : content + text.GetString();
                                }
                            }
                        }

                        break;
                }
            }
        }

        if (doc.RootElement.TryGetProperty("incomplete_details", out var incompleteDetails) &&
            incompleteDetails.ValueKind == JsonValueKind.Object)
        {
            incomplete = incompleteDetails.TryGetProperty("reason", out var reason) &&
                         reason.GetString() == "max_output_tokens";
        }

        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var it)
            ? it.GetInt32()
            : 0;
        // output_tokens already includes reasoning_tokens, so cost lands in the right bucket
        // without adding them twice.
        var outputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var ot)
            ? ot.GetInt32()
            : 0;
        var cachedTokens = usage.ValueKind == JsonValueKind.Object &&
                           usage.TryGetProperty("input_tokens_details", out var details) &&
                           details.ValueKind == JsonValueKind.Object &&
                           details.TryGetProperty("cached_tokens", out var cached) &&
                           cached.ValueKind == JsonValueKind.Number
            ? cached.GetInt32()
            : 0;

        return new LlmCompletionResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            FinishReason = toolCalls.Count > 0 ? LlmFinishReason.ToolCalls
                : incomplete ? LlmFinishReason.MaxTokens
                : LlmFinishReason.Stop,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedTokens
        };
    }

    internal static LlmCompletionResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var finishReason = doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString();

        string? content = message.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null
            ? c.GetString() : null;

        var toolCalls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcArray.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                toolCalls.Add(new ToolCall
                {
                    Id = tc.GetProperty("id").GetString()!,
                    Name = fn.GetProperty("name").GetString()!,
                    ArgumentsJson = fn.GetProperty("arguments").GetString() ?? "{}"
                });
            }
        }

        var finish = finishReason switch
        {
            "tool_calls" => LlmFinishReason.ToolCalls,
            "length" => LlmFinishReason.MaxTokens,
            _ => LlmFinishReason.Stop
        };

        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
        // Reasoning tokens are billed as output tokens, so they are already counted here: a
        // reasoning-era model's cost shows up as a large completion_tokens, not a missing one.
        var outputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
        // OpenAI caches long, stable prompt prefixes automatically; prompt_tokens includes them.
        var cachedTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens_details", out var details) &&
                           details.ValueKind == JsonValueKind.Object && details.TryGetProperty("cached_tokens", out var cached) &&
                           cached.ValueKind == JsonValueKind.Number
            ? cached.GetInt32()
            : 0;

        return new LlmCompletionResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            FinishReason = finish,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedTokens
        };
    }
}

/// <summary>
/// The request shape one OpenAI model speaks: which name it uses for the output cap, whether it
/// accepts sampling parameters at all, and which of OpenAI's two HTTP APIs it must be called on.
/// </summary>
/// <param name="UseMaxCompletionTokens">Reasoning models name the output cap <c>max_completion_tokens</c>.</param>
/// <param name="SupportsSampling">Reasoning models reject a custom <c>temperature</c>/<c>top_p</c>.</param>
/// <param name="RequiresResponsesApi">
/// Some reasoning models refuse function tools on Chat Completions outright, whatever reasoning
/// effort is requested, and accept them only on <c>/v1/responses</c>. The agent runtime is built
/// on tool calling, so for these the endpoint is not a preference but a requirement.
/// </param>
internal sealed record OpenAiCapabilities(
    bool UseMaxCompletionTokens,
    bool SupportsSampling,
    bool RequiresResponsesApi = false)
{
    /// <summary>
    /// Guesses the shape from the model id. A guess, because ids are the only thing available
    /// before the first call — and because the list of reasoning-era models grows every few
    /// months. <see cref="Downgrade"/> corrects it from the API's own 400 and the provider
    /// remembers the answer, so being wrong here costs one rejected request per model, not a
    /// permanently broken provider.
    /// </summary>
    public static OpenAiCapabilities Detect(string model)
    {
        var reasoning = RejectsLegacyParameters(model);
        return new OpenAiCapabilities(
            UseMaxCompletionTokens: reasoning,
            SupportsSampling: !reasoning,
            RequiresResponsesApi: RequiresResponsesApiForTools(model));
    }

    /// <summary>
    /// Reads a rejection and returns the shape the model actually accepts, or null when the error
    /// is about something else (a bad key, a rate limit, a genuine bug in the request) and
    /// re-sending different parameters would only hide it behind a second, more confusing error.
    /// </summary>
    public static OpenAiCapabilities? Downgrade(OpenAiCapabilities current, string errorBody) =>
        TryReadRejectedParameter(errorBody, out var parameter)
            ? parameter switch
            {
                "max_tokens" => current with { UseMaxCompletionTokens = true },
                "max_completion_tokens" => current with { UseMaxCompletionTokens = false },
                // A reasoning model that refuses a temperature accepts the default one, so
                // dropping the parameter is the whole fix. Wording differs by generation
                // ("Unsupported parameter" vs "Unsupported value ... only the default is
                // supported") but both name the field.
                "temperature" or "top_p" => current with { SupportsSampling = false },
                // Refusing tools at any reasoning effort is an endpoint problem, not a parameter
                // one: the same tools are accepted on /v1/responses. Moving there is the fix, and
                // the error says so itself, so it is worth trusting even for a model id whose
                // family this code has never seen.
                "reasoning_effort" => current with { RequiresResponsesApi = true },
                _ => null
            }
            : null;

    /// <summary>
    /// Models that serve function tools only on /v1/responses. OpenAI's own error names both
    /// options ("use /v1/responses or set reasoning_effort to 'none'"), but on these the second
    /// is not actually available — 'none' is rejected as an unsupported value — so tools mean
    /// the Responses endpoint, full stop. They are recognised by family, because the failure
    /// otherwise surfaces as every single agent call failing to produce a tool call.
    /// </summary>
    private static bool RequiresResponsesApiForTools(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        // The GPT-6 generation is served on Responses for tool use. Earlier reasoning models
        // (o-series, GPT-5) do accept tools on Chat Completions, so they are left there.
        return id.StartsWith("gpt-6", StringComparison.Ordinal)
               || (id.StartsWith("gpt-", StringComparison.Ordinal) && MajorVersion(id) >= 6);
    }

    /// <summary>The version number in a "gpt-N..." id, or 0 when there isn't one.</summary>
    private static int MajorVersion(string id)
    {
        if (!id.StartsWith("gpt-", StringComparison.Ordinal))
        {
            return 0;
        }

        var version = id[4..];
        var digits = 0;
        while (digits < version.Length && char.IsAsciiDigit(version[digits]))
        {
            digits++;
        }

        return digits > 0 ? int.Parse(version[..digits]) : 0;
    }

    /// <summary>True for the reasoning-era models: the o-series throughout, GPT-5 and later, and
    /// the Codex models. Everything else — GPT-4o, GPT-4.1, GPT-3.5 — takes the legacy shape.</summary>
    private static bool RejectsLegacyParameters(string model)
    {
        var id = model.Trim().ToLowerInvariant();

        // Gateways and proxies prefix the vendor: "openai/gpt-6-astra", "azure/o3".
        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        if (id.StartsWith("codex", StringComparison.Ordinal))
        {
            return true;
        }

        if (id.StartsWith('o') && id.Length > 1 && char.IsAsciiDigit(id[1]))
        {
            return true;
        }

        // Compare the major version rather than listing prefixes, so GPT-7 needs no change here.
        return MajorVersion(id) >= 5;
    }

    private static bool TryReadRejectedParameter(string errorBody, out string parameter)
    {
        parameter = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("param", out var param) || param.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : string.Empty;
            parameter = param.GetString() ?? string.Empty;
            if (parameter.Length == 0)
            {
                return false;
            }

            // A named code is the reliable signal. The tool refusal is the exception: OpenAI sends
            // code null for it, so fall back to the wording, which still names the parameter and
            // says outright that it is not supported.
            if (code is "unsupported_parameter" or "unsupported_value")
            {
                return true;
            }

            return code is null &&
                   message.Contains("not supported", StringComparison.OrdinalIgnoreCase) &&
                   message.Contains(parameter, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A gateway or proxy can answer with HTML or plain text. Nothing to learn from it.
            return false;
        }
    }
}
