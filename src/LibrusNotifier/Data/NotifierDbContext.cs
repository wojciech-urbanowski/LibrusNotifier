using LibrusNotifier.Librus;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LibrusNotifier.Data;

public sealed class NotifierDbContext(DbContextOptions<NotifierDbContext> options) : DbContext(options)
{
    public DbSet<ElementEntity> Elements => Set<ElementEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<BaselineEntity> Baselines => Set<BaselineEntity>();

    public static NotifierDbContext Create(string databaseFile)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = databaseFile, Pooling = false }.ToString();
        return new NotifierDbContext(new DbContextOptionsBuilder<NotifierDbContext>().UseSqlite(connection).Options);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<AccountKind>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<ModuleKind>().HaveConversion<string>().HaveMaxLength(30);
        configurationBuilder.Properties<RecipientGroup>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<NotificationStatus>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ElementEntity>(element =>
        {
            element.ToTable("Elements");
            element.Property(e => e.Type).HasMaxLength(40);
            element.Property(e => e.Key).HasMaxLength(64);
            element.HasIndex(e => e.Key).IsUnique();
            element.HasIndex(e => e.Type);
            element.HasMany(e => e.Notifications).WithOne(n => n.Element).HasForeignKey(n => n.ElementId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NotificationEntity>(notification =>
        {
            notification.ToTable("Notifications");
            notification.HasIndex(n => new { n.ElementId, n.Group }).IsUnique();
            notification.HasIndex(n => n.Status);
        });
        modelBuilder.Entity<BaselineEntity>(baseline =>
        {
            baseline.ToTable("Baselines");
            baseline.HasKey(b => new { b.Account, b.Module });
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>Fabryka dla narzędzi EF Core (dotnet ef migrations).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NotifierDbContext>
{
    public NotifierDbContext CreateDbContext(string[] args) => NotifierDbContext.Create("librus.db");
}
