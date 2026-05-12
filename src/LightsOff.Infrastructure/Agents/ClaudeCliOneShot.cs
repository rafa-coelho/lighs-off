using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// One-shot wrapper around <c>claude --print --output-format json</c> for stateless calls
/// (Analyze, Plan, Review). Sends the prompt on stdin, reads the wrapped JSON response from
/// stdout, returns the assistant's reply text. Tools are disabled — these calls are pure
/// text-in / text-out, no file edits.
/// </summary>
internal sealed partial class ClaudeCliOneShot
{
    private readonly string _claudePath;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    public ClaudeCliOneShot(string claudePath, ILogger logger, TimeSpan? timeout = null)
    {
        _claudePath = claudePath;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Run claude with the given prompt. Returns the assistant's text reply.
    /// When <paramref name="progress"/> is provided, uses stream-json mode so text chunks
    /// are emitted as they arrive; otherwise falls back to the simpler json mode.
    /// </summary>
    public async Task<string> RunAsync(string prompt, IProgress<string>? progress = null, string? cwd = null, CancellationToken ct = default)
    {
        return progress is not null
            ? await RunStreamingAsync(prompt, progress, cwd, ct).ConfigureAwait(false)
            : await RunBatchAsync(prompt, cwd, ct).ConfigureAwait(false);
    }

    private async Task<string> RunBatchAsync(string prompt, string? cwd, CancellationToken ct)
    {
        var psi = BuildPsi(cwd);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("json");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process.");

        try
        {
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"claude one-shot timed out after {_timeout.TotalSeconds:0}s.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"claude exited with code {proc.ExitCode}. stderr: {Truncate(stderr, 500)}");

            return ExtractResultText(stdout);
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private async Task<string> RunStreamingAsync(string prompt, IProgress<string> progress, string? cwd, CancellationToken ct)
    {
        var psi = BuildPsi(cwd);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");  // claude requires --verbose with --print + stream-json
        psi.ArgumentList.Add("--include-partial-messages");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process.");

        try
        {
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            string? resultText = null;

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (type)
                    {
                        case "stream_event":
                            if (root.TryGetProperty("event", out var ev))
                            {
                                var evType = ev.TryGetProperty("type", out var et) ? et.GetString() : null;
                                if (evType == "content_block_delta" && ev.TryGetProperty("delta", out var delta))
                                {
                                    var dtype = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                                    if (dtype == "text_delta" && delta.TryGetProperty("text", out var dtext))
                                    {
                                        var chunk = dtext.GetString();
                                        if (!string.IsNullOrEmpty(chunk)) progress.Report(chunk);
                                    }
                                }
                            }
                            break;
                        case "result":
                            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                                resultText = r.GetString();
                            break;
                    }
                }
                catch (JsonException) { }
            }

            try { await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"claude streaming timed out after {_timeout.TotalSeconds:0}s.");
            }

            if (proc.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException($"claude exited with code {proc.ExitCode}. stderr: {Truncate(stderr, 500)}");
            }

            return resultText ?? string.Empty;
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private ProcessStartInfo BuildPsi(string? cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _claudePath,
            WorkingDirectory = cwd ?? Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--tools"); psi.ArgumentList.Add("");
        psi.ArgumentList.Add("--no-session-persistence");
        return psi;
    }

    /// <summary>
    /// Run claude expecting a JSON object back. Strips ```json fences if present, then
    /// deserializes to <typeparamref name="T"/>. Throws with a clear message if the assistant
    /// didn't return parseable JSON.
    /// </summary>
    public async Task<T> RunForJsonAsync<T>(string prompt, IProgress<string>? progress = null, string? cwd = null, CancellationToken ct = default)
    {
        var text = await RunAsync(prompt, progress, cwd, ct).ConfigureAwait(false);
        var json = ExtractJsonObject(text)
            ?? throw new InvalidOperationException(
                $"claude reply was not valid JSON. First 200 chars: {Truncate(text, 200)}");

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            return JsonSerializer.Deserialize<T>(json, options)
                ?? throw new InvalidOperationException("claude reply deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"claude reply was not the expected JSON shape: {ex.Message}. First 200 chars: {Truncate(json, 200)}",
                ex);
        }
    }

    /// <summary>
    /// claude --output-format json wraps the assistant text in
    /// <c>{"type":"result","subtype":"success","result":"..."}</c>. Pull out the <c>result</c>.
    /// </summary>
    internal static string ExtractResultText(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                return r.GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("is_error", out var ie) && ie.GetBoolean())
            {
                var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                throw new InvalidOperationException($"claude returned an error: {err}");
            }
        }
        catch (JsonException)
        {
            // Fall through — return raw stdout. Caller decides what to do.
        }
        return stdout;
    }

    /// <summary>
    /// Pulls a JSON object out of a text reply. Handles three common shapes:
    ///   - bare JSON: <c>{...}</c>
    ///   - fenced: <c>```json\n{...}\n```</c> or <c>```\n{...}\n```</c>
    ///   - JSON wrapped in prose: <c>"Here you go: {...}"</c> — we find the first balanced object.
    /// Returns null if no plausible JSON object can be found.
    /// </summary>
    internal static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        // Fenced code block.
        var fence = FencedJsonPattern().Match(text);
        if (fence.Success)
            return fence.Groups[1].Value.Trim();

        // Bare object — scan for the first balanced top-level { ... }, ignoring braces inside strings.
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"```(?:json)?\s*\n([\s\S]*?)\n```", RegexOptions.IgnoreCase)]
    private static partial Regex FencedJsonPattern();
}
