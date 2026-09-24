using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// <see cref="ILLMProvider"/> for locally hosted models via Ollama's native <c>/api/chat</c>
/// endpoint. Native rather than Ollama's OpenAI-compatible endpoint because only the native API
/// lets a request set the context window (<c>num_ctx</c>): Ollama's default is only a few
/// thousand tokens, and it silently truncates longer prompts — an agent's system prompt, tool
/// catalog and recent history would be cut off without any error.
/// Needs a model with tool-calling support (e.g. qwen2.5, qwen3, llama3.1, mistral-nemo).
/// </summary>
public sealed partial class OllamaProvider(HttpClient httpClient, IOptions<LlmOptions> options) : ILLMProvider
{
    public string ProviderName => "Ollama";

    private readonly LlmOptions _options = options.Value;

    // Flipped off if this Ollama server predates the "think" request field and rejects it, so we
    // stop sending it rather than failing every call.
    private volatile bool _sendThinkFlag = options.Value.DisableThinking;

    public async Task<LlmCompletionResponse> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request);
        var (status, responseBody) = await PostAsync(body, cancellationToken);

        if (status == HttpStatusCode.BadRequest && _sendThinkFlag && responseBody.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            _sendThinkFlag = false;
            body.Remove("think");
            (status, responseBody) = await PostAsync(body, cancellationToken);
        }

        if ((int)status is < 200 or > 299)
        {
            throw new InvalidOperationException($"Ollama error {(int)status}: {responseBody}" +
                (responseBody.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
                    ? " — pick a model with tool calling, e.g. qwen2.5:7b, qwen3:8b or llama3.1:8b."
                    : string.Empty));
        }

        return ParseResponse(responseBody);
    }

    private JsonObject BuildBody(LlmCompletionRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model ?? _options.Model,
            ["stream"] = false,
            ["messages"] = BuildMessages(request.Messages),
            ["options"] = new JsonObject
            {
                ["temperature"] = request.Temperature,
                ["num_ctx"] = _options.ContextLength,
                ["num_predict"] = request.MaxTokens
            }
        };

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
        }

        // Reasoning models (qwen3, deepseek-r1, ...) otherwise spend most of each call writing a
        // hidden reasoning trace the runtime would discard anyway. Harmless for other models.
        if (_sendThinkFlag)
        {
            body["think"] = false;
        }

        return body;
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(JsonObject body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync("api/chat", body, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach Ollama at {httpClient.BaseAddress}: {ex.Message}. Is `ollama serve` running, and is " +
                "Llm:BaseUrl / LLM_BASE_URL correct? (From Docker, the host is http://host.docker.internal:11434.)", ex);
        }

        using (response)
        {
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
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
                },
                ["content"] = message.Content ?? string.Empty
            };

            if (message.Role == ChatRole.Tool && message.ToolName is not null)
            {
                obj["tool_name"] = message.ToolName;
            }

            if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 })
            {
                // Ollama takes arguments as a JSON object, not the JSON-encoded string OpenAI uses.
                obj["tool_calls"] = new JsonArray(message.ToolCalls.Select(tc => (JsonNode)new JsonObject
                {
                    ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = ParseArguments(tc.ArgumentsJson) }
                }).ToArray());
            }

            array.Add(obj);
        }

        return array;
    }

    private static JsonNode ParseArguments(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    internal static LlmCompletionResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var message = root.GetProperty("message");

        // Belt and braces for when thinking can't be switched off (older Ollama, or models such as
        // gpt-oss that always reason): inline <think>…</think> is private chain-of-thought, which
        // the runtime never stores or displays (CLAUDE.md section 30). A separate "thinking"
        // field, when present, is likewise ignored.
        string? content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? ThinkBlockRegex().Replace(c.GetString() ?? string.Empty, string.Empty).Trim()
            : null;
        if (string.IsNullOrEmpty(content)) content = null;

        var toolCalls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcArray.EnumerateArray())
            {
                if (!tc.TryGetProperty("function", out var fn) || !fn.TryGetProperty("name", out var name)) continue;

                var arguments = fn.TryGetProperty("arguments", out var args)
                    ? args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : args.GetRawText()
                    : "{}";

                toolCalls.Add(new ToolCall
                {
                    // Ollama doesn't always return call ids; the runtime needs one to pair results.
                    Id = tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()!
                        : "call_" + Guid.NewGuid().ToString("n")[..12],
                    Name = name.GetString()!,
                    ArgumentsJson = arguments
                });
            }
        }

        var doneReason = root.TryGetProperty("done_reason", out var dr) ? dr.GetString() : null;
        var finish = toolCalls.Count > 0
            ? LlmFinishReason.ToolCalls
            : doneReason == "length" ? LlmFinishReason.MaxTokens : LlmFinishReason.Stop;

        return new LlmCompletionResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            FinishReason = finish,
            InputTokens = root.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt32(out var inTok) ? inTok : 0,
            OutputTokens = root.TryGetProperty("eval_count", out var ec) && ec.TryGetInt32(out var outTok) ? outTok : 0
        };
    }

    // An unclosed block (output cut off mid-reasoning) is stripped to the end.
    [GeneratedRegex(@"<think>.*?(?:</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkBlockRegex();
}
