using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// One-shot wrapper around <c>copilot -p "&lt;prompt&gt;" --output-format json --no-ask-user
/// --allow-all-tools</c> for stateless calls (Analyze, Plan, Review, AC review, plan rework).
/// Reads the full JSONL stream, pulls out the final <c>assistant.message</c> content, returns it.
///
/// Tools are LEFT enabled (Copilot's CLI doesn't have a clean "disable all tools" flag the way
/// Claude does — <c>--available-tools</c> takes a list). For one-shot text-in/text-out the agent
/// rarely uses tools when prompted to "respond with JSON", but if it does we still capture the
/// final content correctly.
/// </summary>
internal sealed partial class CopilotCliOneShot
{
    private readonly string _copilotPath;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    public CopilotCliOneShot(string copilotPath, ILogger logger, TimeSpan? timeout = null)
    {
        _copilotPath = copilotPath;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// Run copilot non-interactively. Returns the final assistant text.
    /// When <paramref name="progress"/> is provided, streams <c>assistant.message_delta</c>
    /// chunks as they arrive so the caller can show live output.
    /// </summary>
    public async Task<string> RunAsync(string prompt, IProgress<string>? progress = null, string? cwd = null, IReadOnlyList<string>? addDirs = null, CancellationToken ct = default)
    {
        var psi = BuildPsi(prompt, cwd, addDirs);
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start copilot process.");

        try
        {
            try { proc.StandardInput.Close(); } catch { }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            if (progress is not null)
                return await ReadStreamingAsync(proc, progress, timeoutCts.Token, ct).ConfigureAwait(false);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"copilot one-shot timed out after {_timeout.TotalSeconds:0}s.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"copilot exited with code {proc.ExitCode}. stderr: {Truncate(stderr, 500)}");

            var lines = stdout.Split('\n');
            var content = CopilotJsonlParser.ExtractFinalAssistantContent(lines);
            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException(
                    $"copilot produced no assistant.message in JSONL output. First 500 chars of stdout: {Truncate(stdout, 500)}");

            return content;
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private async Task<string> ReadStreamingAsync(Process proc, IProgress<string> progress, CancellationToken timeoutCt, CancellationToken userCt)
    {
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCt);
        string? finalContent = null;

        string? line;
        try
        {
            while ((line = await proc.StandardOutput.ReadLineAsync(timeoutCt).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "assistant.message_delta" &&
                        root.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("deltaContent", out var delta))
                    {
                        var chunk = delta.GetString();
                        if (!string.IsNullOrEmpty(chunk)) progress.Report(chunk);
                    }
                    else if (type == "assistant.message" &&
                        root.TryGetProperty("data", out var msgData) &&
                        msgData.ValueKind == JsonValueKind.Object &&
                        msgData.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String)
                    {
                        finalContent = c.GetString();
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (timeoutCt.IsCancellationRequested)
        {
            // Timed out while reading. Kill the process and either return what we got or throw.
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (!string.IsNullOrEmpty(finalContent))
                return finalContent;  // process sent the final content before hanging — use it
            if (!userCt.IsCancellationRequested)
                throw new TimeoutException($"copilot one-shot timed out after {_timeout.TotalSeconds:0}s.");
            throw;
        }

        try { await proc.WaitForExitAsync(timeoutCt).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeoutCt.IsCancellationRequested && !userCt.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (!string.IsNullOrEmpty(finalContent))
                return finalContent;
            throw new TimeoutException($"copilot one-shot timed out after {_timeout.TotalSeconds:0}s.");
        }

        // Exit code 130 = SIGINT (process was interrupted). If we already captured the final
        // assistant.message before the interrupt, that content is valid — use it.
        if (proc.ExitCode != 0 && proc.ExitCode != 130)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException($"copilot exited with code {proc.ExitCode}. stderr: {Truncate(stderr, 500)}");
        }

        if (string.IsNullOrEmpty(finalContent))
        {
            if (proc.ExitCode == 130)
                throw new InvalidOperationException("copilot was interrupted (exit 130) before producing output.");
            throw new InvalidOperationException("copilot produced no assistant.message in JSONL output.");
        }

        return finalContent;
    }

    private ProcessStartInfo BuildPsi(string prompt, string? cwd, IReadOnlyList<string>? addDirs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _copilotPath,
            WorkingDirectory = cwd ?? Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--no-ask-user");
        psi.ArgumentList.Add("--allow-all-tools");
        psi.ArgumentList.Add("--no-color");
        if (addDirs is not null)
        {
            foreach (var dir in addDirs)
            {
                psi.ArgumentList.Add("--add-dir");
                psi.ArgumentList.Add(dir);
            }
        }
        return psi;
    }

    /// <summary>
    /// Run copilot expecting a JSON object back. Strips <c>```json</c> fences, locates the first
    /// balanced top-level object, deserializes to <typeparamref name="T"/>. Mirrors the behavior
    /// of <see cref="ClaudeCliOneShot.RunForJsonAsync{T}"/>.
    /// </summary>
    public async Task<T> RunForJsonAsync<T>(string prompt, IProgress<string>? progress = null, string? cwd = null, IReadOnlyList<string>? addDirs = null, CancellationToken ct = default)
    {
        var text = await RunAsync(prompt, progress, cwd, addDirs, ct).ConfigureAwait(false);
        var json = ExtractJsonObject(text)
            ?? throw new InvalidOperationException(
                $"copilot reply was not valid JSON. First 200 chars: {Truncate(text, 200)}");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        try
        {
            return JsonSerializer.Deserialize<T>(json, options)
                ?? throw new InvalidOperationException("copilot reply deserialized to null.");
        }
        catch (JsonException ex)
        {
            // LLMs frequently emit invalid JSON escapes — most often `\<backtick>` from markdown
            // code spans inside string fields. Try sanitizing those and retrying once before
            // giving up. This is best-effort: only safe transforms (no semantic changes).
            var sanitized = SanitizeInvalidEscapes(json);
            if (sanitized != json)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(sanitized, options)
                        ?? throw new InvalidOperationException("copilot reply deserialized to null.");
                }
                catch (JsonException) { /* fall through to original error */ }
            }

            throw new InvalidOperationException(
                $"copilot reply was not the expected JSON shape: {ex.Message}. First 200 chars: {Truncate(json, 200)}",
                ex);
        }
    }

    /// <summary>
    /// Replaces invalid JSON escape sequences (anything other than \" \\ \/ \b \f \n \r \t \uXXXX)
    /// with the bare character. Most common offender: <c>\`</c> from markdown code spans.
    /// </summary>
    internal static string SanitizeInvalidEscapes(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }
            if (c == '"' && (i == 0 || json[i - 1] != '\\')) { inString = false; sb.Append(c); continue; }
            if (c == '\\' && i + 1 < json.Length)
            {
                var next = json[i + 1];
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
                {
                    sb.Append(c);
                    sb.Append(next);
                    i++;
                }
                else
                {
                    // Invalid escape — drop the backslash, keep the next char.
                    sb.Append(next);
                    i++;
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts a JSON object from a markdown/prose reply — same heuristic as the Claude helper:
    /// fenced block, then balanced-brace fallback that respects strings + escapes.
    /// </summary>
    internal static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        var fence = FencedJsonPattern().Match(text);
        if (fence.Success) return fence.Groups[1].Value.Trim();

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
