using System.Text.Json;
using LightsOff.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace LightsOff.Infrastructure.Data;

public class DependencyProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _db;

    public DependencyProfileStore(AppDbContext db) => _db = db;

    public async Task<RepoDependencyProfile?> LoadAsync(string repoName, CancellationToken ct = default)
    {
        var row = await _db.Database
            .SqlQueryRaw<ProfileRow>(
                """SELECT "RepositoryName", "DetectedAt", "AgentUsed", "RuntimesJson", "PackageInstallCommandsJson", "CustomCommandsJson" FROM "RepoDependencyProfiles" WHERE "RepositoryName" = {0}""",
                repoName)
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new RepoDependencyProfile
        {
            RepositoryName = row.RepositoryName,
            DetectedAt = DateTimeOffset.Parse(row.DetectedAt),
            AgentUsed = row.AgentUsed,
            Runtimes = JsonSerializer.Deserialize<List<RuntimeRequirement>>(row.RuntimesJson, Json) ?? new(),
            PackageInstallCommands = JsonSerializer.Deserialize<List<string>>(row.PackageInstallCommandsJson, Json) ?? new(),
            CustomCommands = JsonSerializer.Deserialize<List<string>>(row.CustomCommandsJson ?? "[]", Json) ?? new()
        };
    }

    public async Task SaveAsync(RepoDependencyProfile profile, CancellationToken ct = default)
    {
        var runtimesJson = JsonSerializer.Serialize(profile.Runtimes, Json);
        var pkgJson = JsonSerializer.Serialize(profile.PackageInstallCommands, Json);
        var customJson = JsonSerializer.Serialize(profile.CustomCommands, Json);

        await _db.Database.ExecuteSqlRawAsync("""
            INSERT OR REPLACE INTO "RepoDependencyProfiles"
                ("RepositoryName", "DetectedAt", "AgentUsed", "RuntimesJson", "PackageInstallCommandsJson", "CustomCommandsJson")
            VALUES ({0}, {1}, {2}, {3}, {4}, {5})
            """,
            profile.RepositoryName,
            profile.DetectedAt.ToString("O"),
            profile.AgentUsed,
            runtimesJson,
            pkgJson,
            customJson,
            ct).ConfigureAwait(false);
    }

    // EF Core SqlQueryRaw needs a concrete type to map to
    private class ProfileRow
    {
        public string RepositoryName { get; set; } = "";
        public string DetectedAt { get; set; } = "";
        public string AgentUsed { get; set; } = "";
        public string RuntimesJson { get; set; } = "[]";
        public string PackageInstallCommandsJson { get; set; } = "[]";
        public string? CustomCommandsJson { get; set; } = "[]";
    }
}
