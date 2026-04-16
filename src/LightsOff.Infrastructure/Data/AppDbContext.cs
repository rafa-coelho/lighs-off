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

        base.OnModelCreating(modelBuilder);
    }
}
