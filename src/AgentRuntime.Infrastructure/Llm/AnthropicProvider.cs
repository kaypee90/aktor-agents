using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRuntime.Configuration;
using AgentRuntime.LLM;
using Microsoft.Extensions.Options;

namespace AgentRuntime.Infrastructure.Llm;

/// <summary>
/// Full implementation of <see cref="ILLMProvider"/> against Anthropic's Messages API, including
/// structured tool use (CLAUDE.md section 2/16). This is the "one provider implemented completely";
/// <see cref="OpenAIProvider"/> and <see cref="GeminiProvider"/> plug into the same abstraction.
/// </summary>
public sealed class AnthropicProvider(HttpClient httpClient, IOptions<LlmOptions> options) : ILLMProvider
{
    public string ProviderName => "Anthropic";

    private readonly LlmOptions _options = options.Value;

    public async Task<LlmCompletionResponse> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var systemPrompt = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
        var conversation = request.Messages.Where(m => m.Role != ChatRole.System).ToList();

        var body = new JsonObject
        {
            ["model"] = request.Model ?? _options.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["system"] = systemPrompt,
            ["messages"] = BuildMessages(conversation)
        };

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.JsonSchema)
            }).ToArray());
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        httpRequest.Headers.Add("x-api-key", string.IsNullOrWhiteSpace(_options.ApiKey)
            ? throw new InvalidOperationException("Anthropic API key not configured (Llm:ApiKey / LLM_API_KEY).")
            : _options.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Anthropic API error {(int)response.StatusCode}: {responseBody}");
        }

        return ParseResponse(responseBody);
    }

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> conversation)
    {
        var messages = new JsonArray();
        JsonArray? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is not null)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = pendingToolResults });
                pendingToolResults = null;
            }
        }

        foreach (var message in conversation)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    FlushToolResults();
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message.Content ?? string.Empty })
                    });
                    break;

                case ChatRole.Assistant:
                    FlushToolResults();
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                    }

                    foreach (var call in message.ToolCalls ?? [])
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)
                        });
                    }

                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;

                case ChatRole.Tool:
                    pendingToolResults ??= [];
                    pendingToolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Content ?? string.Empty
                    });
                    break;
            }
        }

        FlushToolResults();
        return messages;
    }

    private static LlmCompletionResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? text = null;
        var toolCalls = new List<ToolCall>();

        if (root.TryGetProperty("content", out var contentArray))
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                {
                    text = (text ?? string.Empty) + block.GetProperty("text").GetString();
                }
                else if (type == "tool_use")
                {
                    toolCalls.Add(new ToolCall
                    {
                        Id = block.GetProperty("id").GetString()!,
                        Name = block.GetProperty("name").GetString()!,
                        ArgumentsJson = block.GetProperty("input").GetRawText()
                    });
                }
            }
        }

        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        var finish = stopReason switch
        {
            "tool_use" => LlmFinishReason.ToolCalls,
            "max_tokens" => LlmFinishReason.MaxTokens,
            _ => LlmFinishReason.Stop
        };

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
        }

        return new LlmCompletionResponse
        {
            Content = text,
            ToolCalls = toolCalls,
            FinishReason = finish,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };
    }
}
