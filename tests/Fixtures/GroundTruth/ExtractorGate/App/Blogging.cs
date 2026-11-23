using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExtractorGate.App;

public class Blog
{
    public int Id { get; set; }
    public bool IsDeleted { get; set; }
    public List<Post> Posts { get; set; } = [];
}

public class Post
{
    public int Id { get; set; }
    public Blog Blog { get; set; } = null!;
}

public class AppDbContext : DbContext
{
    public DbSet<Blog> Blogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Blog>().HasQueryFilter(b => !b.IsDeleted);
    }
}

public class BlogConfiguration : IEntityTypeConfiguration<Blog>
{
    public void Configure(EntityTypeBuilder<Blog> builder)
    {
        builder.HasMany(b => b.Posts).WithOne(p => p.Blog);
    }
}

public class BaseDbContext<TEntity> : DbContext where TEntity : class
{
    public DbSet<TEntity> Entities { get; set; } = null!;
}

public class PostsContext : BaseDbContext<Post>;
