using System.Linq.Expressions;
using CapsuleAudit.Contracts;

namespace CapsuleAudit.Infrastructure;

// Findings 3 and 4 anchor: the EF DbContext that applies the global query
// filter on InstrumentRental (HasQueryFilter) and owns the unique index name
// string-matched by SaveWithLockConflictMessageAsync.
public sealed class ENoteContext : DbContext
{
    private int? GetStoreId() => null;

    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Finding 3 / 4 mechanism: every context.Set<InstrumentRental>() is
        // silently rewritten by this lambda. No call edge reaches it, so the
        // filter is invisible to a call/declare/implement graph without the
        // constraints tier surfacing it.
        modelBuilder.Entity<InstrumentRental>().HasQueryFilter(
            r => r.IsActive && (GetStoreId() == null || r.MusicStoreId == GetStoreId()));

        // Finding 4 coupling: the database name string-matched by
        // SaveWithLockConflictMessageAsync. A rename silently converts a
        // friendly business error into an unhandled DbUpdateException.
        modelBuilder.Entity<InstrumentRental>()
            .HasIndex(nameof(InstrumentRental.InstrumentId), nameof(InstrumentRental.IsActive))
            .IsUnique()
            .HasDatabaseName("UX_InstrumentRental_InstrumentId_ActiveOrApproved");
    }
}

// Finding 4 coupling target: string-matches the unique index database name
// declared in ENoteContext.OnModelCreating.
public sealed class InstrumentRentalRepository
{
    private readonly ENoteContext _context;

    public InstrumentRentalRepository(ENoteContext context) => _context = context;

    public void SaveWithLockConflictMessageAsync(InstrumentRental rental)
    {
        try
        {
            _context.Set<InstrumentRental>().ToList();
        }
        catch (Exception ex) when (ex.Message.Contains("UX_InstrumentRental_InstrumentId_ActiveOrApproved", StringComparison.Ordinal))
        {
            // Friendly business error path. A rename of the index name in
            // ENoteContext.OnModelCreating silently converts this into an
            // unhandled DbUpdateException.
        }
    }
}
