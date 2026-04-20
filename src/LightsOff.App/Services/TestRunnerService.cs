using System.Collections.Concurrent;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.App.Services;

/// <summary>
/// Singleton service that runs test commands in Docker containers independently of the Blazor
/// circuit. Survives page refresh — new page instances reconnect and see current output.
/// </summary>
public class TestRunnerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, ActiveTestRun> _runs = new();

    public event Action<string>? RunUpdated; // fires with ticketKey

    public TestRunnerService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public ActiveTestRun? GetActiveRun(string ticketKey) =>
        _runs.TryGetValue(ticketKey, out var run) ? run : null;

    public bool IsRunning(string ticketKey) =>
        _runs.TryGetValue(ticketKey, out var run) && run.IsRunning;

    public void StartRun(string ticketKey, string containerId, List<RepoTestConfig> configs, string reposRoot)
    {
        if (IsRunning(ticketKey)) return;

        var run = new ActiveTestRun
        {
            TicketKey = ticketKey,
            IsRunning = true,
            Suite = new TestSuiteResult()
        };
        _runs[ticketKey] = run;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();

                foreach (var config in configs)
                {
                    var commands = config.Command?.Split('\n', StringSplitOptions.TrimEntries)
                        .Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new();
                    if (commands.Count == 0) continue;

                    var workDir = string.IsNullOrWhiteSpace(config.WorkingDir)
                        ? $"/workspace/{config.RepositoryName}"
                        : $"/workspace/{config.RepositoryName}/{config.WorkingDir.Trim('/')}";

                    var testRun = new TestRunResult
                    {
                        ConfigName = config.Name,
                        RepositoryName = config.RepositoryName
                    };
                    run.Suite.Results.Add(testRun);

                    foreach (var cmd in commands)
                    {
                        var step = new CommandStep { Command = cmd, IsRunning = true };
                        testRun.Steps.Add(step);
                        RunUpdated?.Invoke(ticketKey);

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

                        try
                        {
                            var exitCode = await docker.ExecStreamAsync(containerId,
                                new[] { "sh", "-c", $"cd {workDir} && {cmd}" },
                                async chunk =>
                                {
                                    step.Output += chunk;
                                    RunUpdated?.Invoke(ticketKey);
                                    await Task.CompletedTask;
                                },
                                cts.Token);
                            sw.Stop();

                            step.IsRunning = false;
                            step.ExitCode = exitCode;
                            step.Passed = exitCode == 0;
                            step.Duration = sw.Elapsed;
                            RunUpdated?.Invoke(ticketKey);

                            if (exitCode != 0) break;
                        }
                        catch (OperationCanceledException)
                        {
                            sw.Stop();
                            step.IsRunning = false;
                            step.ExitCode = -1;
                            step.Passed = false;
                            step.Output += "\n⏱ Timed out after 5 minutes.";
                            step.Duration = sw.Elapsed;
                            RunUpdated?.Invoke(ticketKey);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                run.Error = ex.Message;
            }
            finally
            {
                run.IsRunning = false;
                RunUpdated?.Invoke(ticketKey);
            }
        });
    }
}

public class ActiveTestRun
{
    public string TicketKey { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public TestSuiteResult Suite { get; set; } = new();
    public string? Error { get; set; }
}
