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
/// </summary>
public sealed class OpenAIProvider(HttpClient httpClient, IOptions<LlmOptions> options) : ILLMProvider
{
    public string ProviderName => "OpenAI";

    private readonly LlmOptions _options = options.Value;

    public async Task<LlmCompletionResponse> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model ?? _options.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["messages"] = BuildMessages(request.Messages)
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
            body["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            string.IsNullOrWhiteSpace(_options.ApiKey)
                ? throw new InvalidOperationException("OpenAI API key not configured (Llm:ApiKey / LLM_API_KEY).")
                : _options.ApiKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI API error {(int)response.StatusCode}: {responseBody}");
        }

        return ParseResponse(responseBody);
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

    private static LlmCompletionResponse ParseResponse(string json)
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
        var outputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;

        return new LlmCompletionResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            FinishReason = finish,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };
    }
}
