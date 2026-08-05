using CapsuleAudit.Contracts;

namespace CapsuleAudit.Infrastructure;

// Findings 6 and 7 anchor: the state machine owns the guards and mutators the
// capsule inlines. The capsule must surface both the Cancel->ReturnedAt write
// (Finding 6) and the note-handling asymmetry between Approve and Reject
// (Finding 7).
public sealed class RentalStateMachine
{
    public void ExecuteTransitionAsync(InstrumentRental rental, RentalCommand command)
    {
        switch (command.Kind)
        {
            case RentalCommandKind.Approve:
                Approve(rental, command);
                break;
            case RentalCommandKind.Reject:
                Reject(rental, command);
                break;
            case RentalCommandKind.Cancel:
                Cancel(rental);
                break;
        }
    }

    // Finding 6: Cancel writes ReturnedAt.
    private void Cancel(InstrumentRental rental)
    {
        GuardCancellable(rental);
        rental.State = RentalState.Cancelled;
        rental.ReturnedAt = DateTime.UtcNow;
    }

    private void Reject(InstrumentRental rental, RentalCommand command)
    {
        GuardRejectable(rental);
        rental.State = RentalState.Rejected;
        rental.RejectedAt = DateTime.UtcNow;
        // Finding 7: Reject records the rejection note.
        rental.RejectionNote = command.Note;
    }

    private void Approve(InstrumentRental rental, RentalCommand command)
    {
        GuardApprovable(rental);
        rental.State = RentalState.Approved;
        rental.ApprovedAt = DateTime.UtcNow;
        // Finding 7 asymmetry: Approve drops the note -- it does not persist
        // command.Note into ApprovalNote. Reject records the note; Approve
        // silently discards it.
    }

    private static void GuardCancellable(InstrumentRental r)
    { if (r.State == RentalState.Cancelled) throw new InvalidOperationException(); }

    private static void GuardRejectable(InstrumentRental r)
    { if (r.State is RentalState.Rejected or RentalState.Cancelled) throw new InvalidOperationException(); }

    private static void GuardApprovable(InstrumentRental r)
    { if (r.State is RentalState.Approved or RentalState.Cancelled) throw new InvalidOperationException(); }
}
