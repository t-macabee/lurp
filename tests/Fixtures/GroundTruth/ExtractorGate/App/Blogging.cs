using Microsoft.EntityFrameworkCore;

namespace ExtractorGate.App;

public class Blog
{
    public int Id { get; set; }
    public bool IsDeleted { get; set; }
}

public class AppDbContext : DbContext
{
    public DbSet<Blog> Blogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Blog>().HasQueryFilter(b => !b.IsDeleted);
    }
}
