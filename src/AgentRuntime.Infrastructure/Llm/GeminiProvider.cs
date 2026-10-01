using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// <see cref="ILLMProvider"/> for Google's Gemini API (generateContent), with structured function
/// calling and usage accounting matching <see cref="AnthropicProvider"/> and <see cref="OpenAIProvider"/>.
///
/// <para>Mapping: the system prompt becomes <c>systemInstruction</c>; assistant turns are role
/// "model" with <c>functionCall</c> parts; tool results are <c>functionResponse</c> parts in a "user"
/// turn, grouped like Anthropic's tool_result blocks. Tool schemas go in
/// <c>parametersJsonSchema</c>, which takes full JSON Schema (the older <c>parameters</c> field
/// rejects keywords such as additionalProperties). Thinking models attach a
/// <c>thoughtSignature</c> to their function calls that must be sent back on the next request; it
/// rides along in <see cref="ToolCall.ProviderSignature"/>.</para>
/// </summary>
public sealed class GeminiProvider(HttpClient httpClient, IOptions<LlmOptions> options) : ILLMProvider
{
    public string ProviderName => "Gemini";

    private readonly LlmOptions _options = options.Value;

    public async Task<LlmCompletionResponse> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var model = request.Model ?? _options.Model;
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent.Create(BuildBody(request))
        };
        httpRequest.Headers.Add("x-goog-api-key", string.IsNullOrWhiteSpace(_options.ApiKey)
            ? throw new InvalidOperationException("Gemini API key not configured (Llm:ApiKey / LLM_API_KEY).")
            : _options.ApiKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini API error {(int)response.StatusCode}: {responseBody}");
        }

        return ParseResponse(responseBody);
    }

    internal static JsonObject BuildBody(LlmCompletionRequest request)
    {
        var system = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System);
        var body = new JsonObject
        {
            ["contents"] = BuildContents(request.Messages.Where(m => m.Role != ChatRole.System).ToList()),
            ["generationConfig"] = new JsonObject
            {
                ["maxOutputTokens"] = request.MaxTokens,
                ["temperature"] = request.Temperature
            }
        };

        if (!string.IsNullOrEmpty(system?.Content))
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system.Content }) };
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parametersJsonSchema"] = JsonNode.Parse(t.JsonSchema)
                }).ToArray())
            });
            body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } };
        }

        return body;
    }

    private static JsonArray BuildContents(IReadOnlyList<ChatMessage> conversation)
    {
        var contents = new JsonArray();
        JsonArray? pendingResponses = null;

        void FlushResponses()
        {
            if (pendingResponses is null) return;
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = pendingResponses });
            pendingResponses = null;
        }

        foreach (var message in conversation)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    FlushResponses();
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Content ?? string.Empty })
                    });
                    break;

                case ChatRole.Assistant:
                    FlushResponses();
                    var parts = new JsonArray();
                    if (!string.IsNullOrEmpty(message.Content)) parts.Add(new JsonObject { ["text"] = message.Content });
                    foreach (var call in message.ToolCalls ?? [])
                    {
                        var part = new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["id"] = call.Id,
                                ["name"] = call.Name,
                                ["args"] = ParseObject(call.ArgumentsJson)
                            }
                        };
                        if (!string.IsNullOrEmpty(call.ProviderSignature)) part["thoughtSignature"] = call.ProviderSignature;
                        parts.Add(part);
                    }

                    // A model turn needs at least one part.
                    if (parts.Count == 0) parts.Add(new JsonObject { ["text"] = string.Empty });
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;

                case ChatRole.Tool:
                    pendingResponses ??= [];
                    pendingResponses.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["id"] = message.ToolCallId,
                            ["name"] = message.ToolName ?? "tool",
                            // Gemini wants an object; a tool's JSON object result goes in as-is.
                            ["response"] = ToResponseObject(message.Content)
                        }
                    });
                    break;
            }
        }

        FlushResponses();
        return contents;
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject { ["value"] = JsonNode.Parse(json) };
        }
        catch (JsonException)
        {
            return new JsonObject { ["raw"] = json };
        }
    }

    private static JsonObject ToResponseObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new JsonObject { ["result"] = string.Empty };
        try
        {
            return JsonNode.Parse(content) is JsonObject obj ? obj : new JsonObject { ["result"] = JsonNode.Parse(content) };
        }
        catch (JsonException)
        {
            return new JsonObject { ["result"] = content };
        }
    }

    internal static LlmCompletionResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? text = null;
        var toolCalls = new List<ToolCall>();
        string? finishReason = null;

        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            finishReason = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    // Thought summaries are never stored or shown (CLAUDE.md section 30).
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;

                    if (part.TryGetProperty("functionCall", out var call))
                    {
                        toolCalls.Add(new ToolCall
                        {
                            Id = call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } callId
                                ? callId
                                : "call_" + Guid.NewGuid().ToString("n")[..12],
                            Name = call.GetProperty("name").GetString()!,
                            ArgumentsJson = call.TryGetProperty("args", out var args) ? args.GetRawText() : "{}",
                            ProviderSignature = part.TryGetProperty("thoughtSignature", out var sig) ? sig.GetString() : null
                        });
                    }
                    else if (part.TryGetProperty("text", out var t))
                    {
                        text = (text ?? string.Empty) + t.GetString();
                    }
                }
            }
        }
        else if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block))
        {
            throw new InvalidOperationException($"Gemini blocked the prompt: {block.GetString()}");
        }

        var finish = toolCalls.Count > 0 ? LlmFinishReason.ToolCalls
            : finishReason == "MAX_TOKENS" ? LlmFinishReason.MaxTokens
            : finishReason is null or "STOP" ? LlmFinishReason.Stop
            : LlmFinishReason.Error;

        int Usage(JsonElement usage, string name) =>
            usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        int prompt = 0, cached = 0, output = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            // promptTokenCount includes the cached part; thinking tokens are billed as output.
            prompt = Usage(usage, "promptTokenCount") + Usage(usage, "toolUsePromptTokenCount");
            cached = Usage(usage, "cachedContentTokenCount");
            output = Usage(usage, "candidatesTokenCount") + Usage(usage, "thoughtsTokenCount");
        }

        return new LlmCompletionResponse
        {
            Content = text,
            ToolCalls = toolCalls,
            FinishReason = finish,
            InputTokens = prompt,
            OutputTokens = output,
            CachedInputTokens = cached
        };
    }
}
