using LightsOff.Core.Abstractions;

namespace LightsOff.Infrastructure.Agents;

public class CodeAgentProvider : ICodeAgentProvider
{
    private readonly IReadOnlyList<ICodeAgent> _agents;
    private readonly IAppSettingsStore _settings;

    public CodeAgentProvider(IEnumerable<ICodeAgent> agents, IAppSettingsStore settings)
    {
        _agents = agents.ToList();
        _settings = settings;
    }

    public IReadOnlyList<ICodeAgent> AvailableAgents => _agents;

    public ICodeAgent GetDefaultAgent()
    {
        var defaultId = _settings.GetAsync(AppSettingsKeys.DefaultAgent).GetAwaiter().GetResult();
        return GetAgent(defaultId);
    }

    public ICodeAgent GetAgent(string? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return _agents.First(a => a.Id == "local");
        }

        var agent = _agents.FirstOrDefault(a =>
            string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            throw new InvalidOperationException(
                $"Code agent '{agentId}' not found. Available: {string.Join(", ", _agents.Select(a => a.Id))}");
        }

        if (!agent.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Code agent '{agentId}' is not available — it requires an API key. Configure it in Settings, or switch to 'local'.");
        }

        return agent;
    }
}
