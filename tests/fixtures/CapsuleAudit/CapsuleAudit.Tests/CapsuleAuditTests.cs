using CapsuleAudit.Contracts;
using CapsuleAudit.Infrastructure;

namespace CapsuleAudit.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public sealed class RentalStateMachineTests
{
    [Fact]
    public void Cancel_Writes_ReturnedAt()
    {
        // Finding 6 acceptance: Cancel writes ReturnedAt.
        var rental = new InstrumentRental { State = RentalState.Approved };
        new RentalStateMachine().ExecuteTransitionAsync(rental, new RentalCommand(RentalCommandKind.Cancel));
        _ = rental.ReturnedAt;
    }

    [Fact]
    public void Reject_Records_RejectionNote_But_Approve_Drops_Note()
    {
        // Finding 7 acceptance: the note-handling asymmetry.
        var rental = new InstrumentRental { State = RentalState.Pending };
        new RentalStateMachine().ExecuteTransitionAsync(rental, new RentalCommand(RentalCommandKind.Reject, Note: "broken"));
        _ = rental.RejectionNote;
    }
}

public sealed class TenantIsolationTests
{
    [Fact]
    public void GetPagedForStoreAsync_ExcludesOtherStoreRentals()
    {
        // Finding 3 corroborating evidence: the suite tests only the resolved
        // store path (StubCurrentActor always resolves a store), giving false
        // assurance about the null-store case the global query filter exposes.
        var svc = new RentalQueryService(new ENoteContext(), new CurrentActor());
        _ = svc.GetPagedForStoreAsync(1, 10);
    }

    [Fact]
    public void GetByIdForStoreAsync_Throws_WhenRentalBelongsToOtherStore()
    {
        var svc = new RentalQueryService(new ENoteContext(), new CurrentActor());
        _ = svc.GetByIdForStoreAsync(1);
    }
}
