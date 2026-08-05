// Stand-in framework base types and fluent APIs. Adapters match by short name
// only (see src/Adapters/*), so these empty shells are sufficient to exercise
// the BackgroundService, DbContext, and EF model-configuration code paths
// without pulling in the real ASP.NET Core / EF Core packages. Mirrors the
// stand-in strategy in tests/fixtures/Sample.

using System.Linq.Expressions;

namespace CapsuleAudit.Contracts;

// -- Hosting ---------------------------------------------------------------
// ExecuteAsync is `protected` (not `protected internal` as in the real
// BackgroundService) so a derived class in a different assembly can override
// it without the cross-assembly internal-accessibility mismatch. The
// ASP.NET Core adapter identifies the override via the Overrides edge, not
// the accessibility modifier.
public abstract class BackgroundService
{
    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);
}

public interface IHostedService { }

// -- EF Core ---------------------------------------------------------------
public abstract class DbContext
{
    public DbSet<TEntity> Set<TEntity>() where TEntity : class => new();
}

public sealed class DbSet<TEntity> where TEntity : class
{
    private readonly List<TEntity> _rows = [];

    public List<TEntity> Where(Func<TEntity, bool> predicate) => _rows.Where(predicate).ToList();
    public List<TEntity> ToList() => [.. _rows];
    public TEntity? FirstOrDefault() => _rows.FirstOrDefault();
}

public interface IEntityTypeConfiguration<TEntity> where TEntity : class
{
    void Configure(EntityTypeBuilder<TEntity> builder);
}

public sealed class ModelBuilder
{
    public EntityTypeBuilder<TEntity> Entity<TEntity>() where TEntity : class => new();
}

public sealed class EntityTypeBuilder<TEntity> where TEntity : class
{
    public EntityTypeBuilder<TEntity> HasQueryFilter(Expression<Func<TEntity, bool>> filter) => this;

    public IndexBuilder HasIndex(params string[] propertyNames) => new();
}

public sealed class IndexBuilder
{
    public IndexBuilder IsUnique() => this;
    public IndexBuilder HasDatabaseName(string name) => this;
}
