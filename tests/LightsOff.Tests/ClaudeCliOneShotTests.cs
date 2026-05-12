using LightsOff.Infrastructure.Agents;

namespace LightsOff.Tests;

/// <summary>
/// Unit tests for the static parsing helpers used by <see cref="ClaudeCliOneShot"/>.
/// The actual subprocess invocation requires a live <c>claude</c> binary and is covered
/// by manual end-to-end testing instead.
/// </summary>
public class ClaudeCliOneShotTests
{
    // ── ExtractResultText: pulls the assistant text out of the JSON wrapper ──

    [Fact]
    public void ExtractResultText_returns_result_field_for_success_wrapper()
    {
        var stdout = """{"type":"result","subtype":"success","is_error":false,"result":"hello, world"}""";
        Assert.Equal("hello, world", ClaudeCliOneShot.ExtractResultText(stdout));
    }

    [Fact]
    public void ExtractResultText_throws_on_error_wrapper()
    {
        var stdout = """{"type":"result","subtype":"error","is_error":true,"error":"rate limited"}""";
        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCliOneShot.ExtractResultText(stdout));
        Assert.Contains("rate limited", ex.Message);
    }

    [Fact]
    public void ExtractResultText_returns_raw_when_not_json()
    {
        Assert.Equal("not json at all", ClaudeCliOneShot.ExtractResultText("not json at all"));
    }

    // ── ExtractJsonObject: pulls JSON out of possibly-fenced or prose-wrapped reply ──

    [Fact]
    public void ExtractJsonObject_returns_bare_object()
    {
        var input = """{"a":1,"b":"two"}""";
        Assert.Equal(input, ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_strips_json_code_fence()
    {
        var input = "```json\n{\"a\":1}\n```";
        Assert.Equal("{\"a\":1}", ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_strips_unlabelled_code_fence()
    {
        var input = "```\n{\"a\":1}\n```";
        Assert.Equal("{\"a\":1}", ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_finds_first_object_when_wrapped_in_prose()
    {
        var input = "Sure! Here you go: {\"a\":1,\"b\":2}\n\nLet me know if you want changes.";
        Assert.Equal("{\"a\":1,\"b\":2}", ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_handles_nested_objects()
    {
        var input = "{\"outer\":{\"inner\":{\"deep\":true}},\"sibling\":1}";
        Assert.Equal(input, ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_ignores_braces_inside_strings()
    {
        var input = "{\"text\":\"this has } in it\",\"x\":1}";
        Assert.Equal(input, ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_handles_escaped_quotes()
    {
        var input = "{\"text\":\"she said \\\"hi}\\\" and left\"}";
        Assert.Equal(input, ClaudeCliOneShot.ExtractJsonObject(input));
    }

    [Fact]
    public void ExtractJsonObject_returns_null_when_no_object()
    {
        Assert.Null(ClaudeCliOneShot.ExtractJsonObject("just plain prose, no json here"));
        Assert.Null(ClaudeCliOneShot.ExtractJsonObject(""));
    }

    [Fact]
    public void ExtractJsonObject_returns_null_when_object_is_unbalanced()
    {
        // Opening brace but no close — claude was cut off.
        Assert.Null(ClaudeCliOneShot.ExtractJsonObject("Here: {\"a\":1, \"b\": "));
    }
}
