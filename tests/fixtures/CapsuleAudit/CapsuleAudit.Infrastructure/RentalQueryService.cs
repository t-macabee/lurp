using CapsuleAudit.Contracts;

namespace CapsuleAudit.Infrastructure;

// Finding 3 (store reads fail open) and Finding 4 (unreachable guard) anchor.
// currentActor always resolves a store via StubCurrentActor, so the null-store
// path is untested -- the audit's TenantIsolationTests corroborated this.
public interface ICurrentActor
{
    int? StoreId { get; }
}

public sealed class CurrentActor : ICurrentActor
{
    public int? StoreId => 1; // always resolves a store; null-store path untested.
}

public sealed class RentalQueryService
{
    private readonly ENoteContext _context;
    private readonly ICurrentActor _currentActor;

    public RentalQueryService(ENoteContext context, ICurrentActor currentActor)
    {
        _context = context;
        _currentActor = currentActor;
    }

    // Finding 3: silently filtered by the global query filter in ENoteContext.
    // Finding 4: GuardInstrumentActive is unreachable under the filter's logic
    // when GetStoreId() == null (the filter already returns all active rows
    // regardless of store), so the guard never fires for the null-store case.
    public RentalPage GetPagedForStoreAsync(int page, int pageSize)
    {
        GuardInstrumentActive();
        var rows = _context.Set<InstrumentRental>().ToList();
        return new RentalPage(rows, rows.Count);
    }

    public InstrumentRental? GetByIdForStoreAsync(int id)
    {
        GuardInstrumentActive();
        return _context.Set<InstrumentRental>().FirstOrDefault();
    }

    // Finding 5 anchor: an audit-only variant that no host, handler, or test
    // ever calls. Dead code has no call edges, so no capsule tier reaches it --
    // an accepted, declared boundary of a call/declare/implement graph.
    public RentalPage GetForStoreAuditAsync(int page, int pageSize)
    {
        var rows = _context.Set<InstrumentRental>().ToList();
        return new RentalPage(rows, rows.Count);
    }

    private void GuardInstrumentActive()
    {
        // Unreachable for the null-store path because the global query filter
        // (GetStoreId() == null || r.MusicStoreId == GetStoreId()) returns rows
        // from every store when GetStoreId() is null, then IsActive filters the
        // already-store-scoped set -- the guard never sees an inactive row that
        // would otherwise be excluded by store scoping.
        if (_currentActor.StoreId == null)
            throw new InvalidOperationException("Actor has no store.");
    }
}
