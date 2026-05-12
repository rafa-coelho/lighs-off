using System.Text.Json;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// Pure translator: one stream-json line in → zero-or-more <see cref="AgentEvent"/> out.
/// Extracted from <see cref="SubprocessAgentSession"/> so the event mapping is testable without
/// spawning a real claude process. The parser is defensive — any shape it doesn't recognise
/// is surfaced as a raw <see cref="LogChunkEvent"/> rather than dropped or thrown.
/// </summary>
internal static class ClaudeStreamJsonParser
{
    public static IEnumerable<AgentEvent> Parse(string line, DateTimeOffset now)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new[] { new LogChunkEvent(now, "stdout", line + "\n") };
        }
        return ParseRoot(root, line, now);
    }

    private static IEnumerable<AgentEvent> ParseRoot(JsonElement root, string rawLine, DateTimeOffset now)
    {
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "assistant":
            {
                if (!root.TryGetProperty("message", out var msg)) yield break;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;

                foreach (var block in content.EnumerateArray())
                {
                    var btype = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                    if (btype == "text" && block.TryGetProperty("text", out var text))
                    {
                        var s = text.GetString();
                        if (!string.IsNullOrEmpty(s))
                            yield return new ChatReplyEvent(now, s, IsFinal: true);
                    }
                    else if (btype == "tool_use")
                    {
                        var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                        var args = block.TryGetProperty("input", out var i) ? i.GetRawText() : "{}";
                        yield return new ToolCallEvent(now, name, args);
                    }
                }
                break;
            }

            case "stream_event":
            {
                if (!root.TryGetProperty("event", out var ev)) yield break;
                var evType = ev.TryGetProperty("type", out var et) ? et.GetString() : null;
                if (evType == "content_block_delta" && ev.TryGetProperty("delta", out var delta))
                {
                    var dtype = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    if (dtype == "text_delta" && delta.TryGetProperty("text", out var dtext))
                    {
                        var s = dtext.GetString();
                        if (!string.IsNullOrEmpty(s))
                            yield return new ChatReplyEvent(now, s, IsFinal: false);
                    }
                }
                break;
            }

            case "user":
            {
                if (!root.TryGetProperty("message", out var msg)) yield break;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result")
                    {
                        var txt = block.TryGetProperty("content", out var c)
                            ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText())
                            : null;
                        if (!string.IsNullOrEmpty(txt))
                            yield return new LogChunkEvent(now, "stdout", txt + "\n");
                    }
                }
                break;
            }

            case "result":
            {
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                var isError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                string? err = null;
                if (isError && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                    err = r.GetString();
                yield return new SessionFinishedEvent(now, Success: !isError, Error: err ?? (subtype == "success" ? null : subtype));
                break;
            }

            case "system":
            {
                var subtypeSys = root.TryGetProperty("subtype", out var ss) ? ss.GetString() : null;
                if (!string.IsNullOrEmpty(subtypeSys))
                    yield return new LogChunkEvent(now, "stdout", $"[system:{subtypeSys}]\n");
                break;
            }

            default:
                yield return new LogChunkEvent(now, "stdout", rawLine + "\n");
                break;
        }
    }

    public static string SerializeInput(AgentInput input) => input switch
    {
        AgentTaskInput t => BuildUserMessage(t.Instruction),
        AgentChatInput c => BuildUserMessage(c.Message),
        AgentAnswerInput a => BuildUserMessage($"(answer to {a.QuestionId}) {a.Answer}"),
        _ => throw new NotSupportedException($"Input type {input.GetType().Name} is not supported.")
    };

    private static string BuildUserMessage(string text)
    {
        var obj = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "text", text } }
            }
        };
        return JsonSerializer.Serialize(obj);
    }
}
