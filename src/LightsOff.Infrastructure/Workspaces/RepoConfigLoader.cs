using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LightsOff.Infrastructure.Workspaces;

public class RepoConfigLoader
{
    private static readonly string[] ConfigFileNames = new[] { ".lightsoff.yaml", ".lightsoff.yml" };
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ILogger<RepoConfigLoader> _logger;

    public RepoConfigLoader(ILogger<RepoConfigLoader> logger)
    {
        _logger = logger;
    }

    public RepoConfig? TryLoad(string repoPath)
    {
        foreach (var name in ConfigFileNames)
        {
            var path = Path.Combine(repoPath, name);
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                var config = Yaml.Deserialize<RepoConfig>(text);
                if (config is not null)
                {
                    _logger.LogInformation("Loaded {File} from {RepoPath}", name, repoPath);
                    return config;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse {File} in {RepoPath}; ignoring", name, repoPath);
            }
        }
        return null;
    }
}
