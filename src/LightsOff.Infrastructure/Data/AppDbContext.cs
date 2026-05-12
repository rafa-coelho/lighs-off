using LightsOff.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LightsOff.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<TicketRepositoryLink> TicketRepositoryLinks => Set<TicketRepositoryLink>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<RepoTestConfig> RepoTestConfigs => Set<RepoTestConfig>();
    public DbSet<StepRun> StepRuns => Set<StepRun>();
    public DbSet<StepEventLogEntry> StepEventLog => Set<StepEventLogEntry>();
    public DbSet<RepoAnalysisCacheEntry> RepoAnalysisCache => Set<RepoAnalysisCacheEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Repository>(b =>
        {
            b.HasKey(r => r.Name);
            b.Property(r => r.Name).HasMaxLength(200);
            b.Property(r => r.CloneUrl).IsRequired();
            b.Property(r => r.DefaultBranch).HasMaxLength(200);
        });

        modelBuilder.Entity<Workspace>(b =>
        {
            b.HasKey(w => w.TicketKey);
            b.Property(w => w.TicketKey).HasMaxLength(50);
            b.Property(w => w.Status).HasConversion<string>();
            b.Property(w => w.CreatedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            b.HasMany(w => w.Links)
                .WithOne()
                .HasForeignKey(l => l.TicketKey)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketRepositoryLink>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.TicketKey).HasMaxLength(50);
            b.Property(l => l.RepositoryName).HasMaxLength(200);
            b.HasIndex(l => new { l.TicketKey, l.RepositoryName }).IsUnique();
        });

        modelBuilder.Entity<AppSetting>(b =>
        {
            b.HasKey(s => s.Key);
            b.Property(s => s.Key).HasMaxLength(100);
        });

        modelBuilder.Entity<RepoTestConfig>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.RepositoryName).HasMaxLength(200);
            b.Property(t => t.Name).HasMaxLength(200);
            b.HasIndex(t => t.RepositoryName);
        });

        modelBuilder.Entity<StepRun>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasMaxLength(64);
            b.Property(s => s.TicketKey).HasMaxLength(50);
            b.Property(s => s.AgentId).HasMaxLength(50);
            b.Property(s => s.ParentRunId).HasMaxLength(64);
            b.Property(s => s.Step).HasConversion<string>();
            b.Property(s => s.Status).HasConversion<string>();
            b.Property(s => s.StartedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            b.Property(s => s.FinishedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            b.HasIndex(s => new { s.TicketKey, s.Step });
            b.HasIndex(s => s.ParentRunId);
        });

        modelBuilder.Entity<StepEventLogEntry>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.StepRunId).HasMaxLength(64);
            b.Property(e => e.Kind).HasMaxLength(80);
            b.Property(e => e.At).HasConversion<DateTimeOffsetToBinaryConverter>();
            b.HasIndex(e => new { e.StepRunId, e.Seq }).IsUnique();
        });

        modelBuilder.Entity<RepoAnalysisCacheEntry>(b =>
        {
            b.HasKey(c => new { c.RepoName, c.HeadSha });
            b.Property(c => c.RepoName).HasMaxLength(200);
            b.Property(c => c.HeadSha).HasMaxLength(64);
            b.Property(c => c.AgentId).HasMaxLength(50);
            b.Property(c => c.AnalyzedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
        });

        base.OnModelCreating(modelBuilder);
    }
}
