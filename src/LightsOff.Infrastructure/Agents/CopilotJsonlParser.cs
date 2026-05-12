using System.Text.Json;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// Parses one line of GitHub Copilot CLI's <c>--output-format json</c> stream into the neutral
/// <see cref="AgentEvent"/> shape. Copilot emits JSONL — one JSON object per line — with a
/// <c>type</c> discriminator and a <c>data</c> payload. The set of types we care about:
///
/// <list type="bullet">
/// <item><c>assistant.message_delta</c> — streaming text chunks (<c>data.deltaContent</c>)</item>
/// <item><c>assistant.message</c> — final assistant reply (<c>data.content</c> + <c>data.toolRequests</c>)</item>
/// <item><c>tool.*</c> — tool invocations (mapped to <see cref="ToolCallEvent"/>)</item>
/// <item><c>result</c> — terminator with <c>exitCode</c> and <c>sessionId</c></item>
/// </list>
///
/// Everything else (session.warning, session.mcp_*, assistant.turn_*, etc.) is either ephemeral
/// or low-signal — we surface them as quiet stdout log lines so the user can see what's
/// happening in debug mode but they don't clutter the chat.
/// </summary>
internal static class CopilotJsonlParser
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
        var data = root.TryGetProperty("data", out var d) ? d : default;
        var ephemeral = root.TryGetProperty("ephemeral", out var ep) && ep.GetBoolean();

        switch (type)
        {
            case "assistant.message_delta":
            {
                if (data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("deltaContent", out var delta))
                {
                    var s = delta.GetString();
                    if (!string.IsNullOrEmpty(s))
                        yield return new ChatReplyEvent(now, s, IsFinal: false);
                }
                break;
            }

            case "assistant.message":
            {
                if (data.ValueKind != JsonValueKind.Object) yield break;

                if (data.TryGetProperty("content", out var content))
                {
                    var s = content.GetString();
                    if (!string.IsNullOrEmpty(s))
                        yield return new ChatReplyEvent(now, s, IsFinal: true);
                }

                // Tool requests come embedded on the assistant.message envelope.
                if (data.TryGetProperty("toolRequests", out var requests) && requests.ValueKind == JsonValueKind.Array)
                {
                    foreach (var req in requests.EnumerateArray())
                    {
                        var name = req.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                        var args = req.TryGetProperty("input", out var i) ? i.GetRawText()
                                 : req.TryGetProperty("arguments", out var a) ? a.GetRawText()
                                 : "{}";
                        yield return new ToolCallEvent(now, name, args);
                    }
                }
                break;
            }

            case "tool.call":
            case "tool.use":
            case "tool.invoke":
            {
                if (data.ValueKind != JsonValueKind.Object) yield break;
                var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                var args = data.TryGetProperty("input", out var i) ? i.GetRawText()
                         : data.TryGetProperty("arguments", out var a) ? a.GetRawText()
                         : "{}";
                yield return new ToolCallEvent(now, name, args);
                break;
            }

            case "tool.result":
            case "tool.output":
            {
                if (data.ValueKind != JsonValueKind.Object) yield break;
                var output = data.TryGetProperty("content", out var c)
                    ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText())
                    : (data.TryGetProperty("output", out var o)
                        ? (o.ValueKind == JsonValueKind.String ? o.GetString() : o.GetRawText())
                        : null);
                if (!string.IsNullOrEmpty(output))
                    yield return new LogChunkEvent(now, "stdout", output + "\n");
                break;
            }

            case "result":
            {
                // Terminator — copilot exits after this event. Map to SessionFinished.
                var exitCode = root.TryGetProperty("exitCode", out var ec) && ec.TryGetInt32(out var code) ? code : 0;
                var success = exitCode == 0;
                string? error = null;
                if (!success && root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                    error = errEl.GetString();
                yield return new SessionFinishedEvent(now, success, error);
                break;
            }

            case "session.warning":
            case "session.error":
            {
                var msg = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m)
                    ? m.GetString() ?? type
                    : type;
                yield return new LogChunkEvent(now, type == "session.error" ? "stderr" : "stdout", $"[{type}] {msg}\n");
                break;
            }

            // Ephemeral envelopes (assistant.turn_*, session.mcp_*, session.skills_loaded, etc.)
            // get suppressed entirely — they're internal lifecycle that the user doesn't care about.
            case "user.message":
            case "assistant.turn_start":
            case "assistant.turn_end":
            case "assistant.message_start":
            case "session.mcp_server_status_changed":
            case "session.mcp_servers_loaded":
            case "session.skills_loaded":
            case "session.tools_updated":
                break;

            default:
                if (!ephemeral)
                    yield return new LogChunkEvent(now, "stdout", rawLine + "\n");
                break;
        }
    }

    /// <summary>
    /// One-shot helpers reading a full JSONL response (used by <c>CopilotCliOneShot</c>): pulls
    /// the final assistant content (the <c>assistant.message</c> with <c>content</c>) out of a
    /// stream of lines. Returns null if no assistant.message is present.
    /// </summary>
    public static string? ExtractFinalAssistantContent(IEnumerable<string> lines)
    {
        string? lastFinal = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "assistant.message") continue;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            if (data.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s)) lastFinal = s;
            }
        }
        return lastFinal;
    }
}
