using System.Text.Json;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Agents;

namespace LightsOff.Tests;

public class ClaudeStreamJsonParserTests
{
    private static readonly DateTimeOffset At = new(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void System_init_is_surfaced_as_log_line()
    {
        var json = """{"type":"system","subtype":"init","session_id":"abc"}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        Assert.Single(events);
        var log = Assert.IsType<LogChunkEvent>(events[0]);
        Assert.Equal("stdout", log.Stream);
        Assert.Contains("init", log.Text);
    }

    [Fact]
    public void Assistant_text_becomes_final_chat_reply()
    {
        var json = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello there"}]}}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var reply = Assert.IsType<ChatReplyEvent>(Assert.Single(events));
        Assert.Equal("hello there", reply.Text);
        Assert.True(reply.IsFinal);
    }

    [Fact]
    public void Assistant_tool_use_becomes_tool_call_event()
    {
        var json = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var tool = Assert.IsType<ToolCallEvent>(Assert.Single(events));
        Assert.Equal("Bash", tool.Tool);
        using var args = JsonDocument.Parse(tool.ArgsJson);
        Assert.Equal("ls -la", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Assistant_mixed_content_emits_in_order()
    {
        var json = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"first"},{"type":"tool_use","id":"t1","name":"Read","input":{"path":"/tmp/x"}},{"type":"text","text":"second"}]}}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        Assert.Equal(3, events.Count);
        Assert.IsType<ChatReplyEvent>(events[0]);
        Assert.IsType<ToolCallEvent>(events[1]);
        Assert.IsType<ChatReplyEvent>(events[2]);
    }

    [Fact]
    public void Stream_event_partial_text_becomes_non_final_reply()
    {
        var json = """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"hel"}}}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var reply = Assert.IsType<ChatReplyEvent>(Assert.Single(events));
        Assert.Equal("hel", reply.Text);
        Assert.False(reply.IsFinal);
    }

    [Fact]
    public void User_tool_result_becomes_log_chunk()
    {
        var json = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file contents"}]}}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var log = Assert.IsType<LogChunkEvent>(Assert.Single(events));
        Assert.Contains("file contents", log.Text);
    }

    [Fact]
    public void Result_success_becomes_session_finished_success()
    {
        var json = """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var fin = Assert.IsType<SessionFinishedEvent>(Assert.Single(events));
        Assert.True(fin.Success);
        Assert.Null(fin.Error);
    }

    [Fact]
    public void Result_error_becomes_session_finished_failure()
    {
        var json = """{"type":"result","subtype":"error","is_error":true,"result":"oops"}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var fin = Assert.IsType<SessionFinishedEvent>(Assert.Single(events));
        Assert.False(fin.Success);
        Assert.Equal("oops", fin.Error);
    }

    [Fact]
    public void Non_json_line_is_preserved_as_raw_stdout()
    {
        var events = ClaudeStreamJsonParser.Parse("hello, not json", At).ToList();

        var log = Assert.IsType<LogChunkEvent>(Assert.Single(events));
        Assert.Equal("stdout", log.Stream);
        Assert.Contains("hello, not json", log.Text);
    }

    [Fact]
    public void Unknown_envelope_falls_through_as_raw()
    {
        var json = """{"type":"future_event","payload":"whatever"}""";
        var events = ClaudeStreamJsonParser.Parse(json, At).ToList();

        var log = Assert.IsType<LogChunkEvent>(Assert.Single(events));
        Assert.Contains("future_event", log.Text);
    }

    [Fact]
    public void SerializeInput_produces_canonical_user_envelope()
    {
        var json = ClaudeStreamJsonParser.SerializeInput(new AgentChatInput("what's next?"));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var block = content.EnumerateArray().Single();
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("what's next?", block.GetProperty("text").GetString());
    }

    [Fact]
    public void SerializeInput_task_and_answer_use_same_user_envelope_shape()
    {
        var task = ClaudeStreamJsonParser.SerializeInput(new AgentTaskInput("do X", Array.Empty<string>()));
        var answer = ClaudeStreamJsonParser.SerializeInput(new AgentAnswerInput("q1", "yes"));

        foreach (var payload in new[] { task, answer })
        {
            using var doc = JsonDocument.Parse(payload);
            Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
            var block = doc.RootElement
                .GetProperty("message").GetProperty("content")
                .EnumerateArray().Single();
            Assert.Equal("text", block.GetProperty("type").GetString());
        }
    }
}
