namespace CapsuleAudit.Contracts;

public enum RentalState
{
    Pending,
    Approved,
    Rejected,
    Cancelled
}

public enum RentalCommandKind
{
    Approve,
    Reject,
    Cancel
}

public sealed record RentalCommand(RentalCommandKind Kind, string? Note = null);

public sealed class InstrumentRental
{
    public int Id { get; set; }
    public int InstrumentId { get; set; }
    public int? MusicStoreId { get; set; }
    public bool IsActive { get; set; }
    public RentalState State { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionNote { get; set; }
    public string? ApprovalNote { get; set; }
}

public sealed record RentalPage(IReadOnlyList<InstrumentRental> Items, int Total);
