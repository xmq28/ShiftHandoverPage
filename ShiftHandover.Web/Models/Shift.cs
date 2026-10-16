namespace ShiftHandover.Web.Models;

public class Shift
{
    public int Id { get; private set; }
    public string Title { get; set; } = string.Empty;
    public DateOnly ShiftDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public ShiftStatus Status { get; private set; } = ShiftStatus.Available;
    public string? ClaimedBySupervisorId { get; private set; }
    public ApplicationUser? ClaimedBySupervisor { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public ICollection<Accident> Accidents { get; } = new List<Accident>();
    public ICollection<Incident> Incidents { get; } = new List<Incident>();
    public ICollection<ManpowerRecord> ManpowerRecords { get; } = new List<ManpowerRecord>();
    public ShiftReportDelivery? ReportDelivery { get; private set; }

    public bool CanBeEditedBy(string supervisorId) =>
        Status == ShiftStatus.Claimed && ClaimedBySupervisorId == supervisorId;

    public void Claim(string supervisorId, DateTimeOffset claimedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supervisorId);

        if (Status != ShiftStatus.Available || ClaimedBySupervisorId is not null)
        {
            throw new InvalidOperationException("Only an available shift can be claimed.");
        }

        Status = ShiftStatus.Claimed;
        ClaimedBySupervisorId = supervisorId;
        ClaimedAt = claimedAt;
    }

    public void Close(string supervisorId, DateTimeOffset closedAt)
    {
        if (Status != ShiftStatus.Claimed || ClaimedBySupervisorId != supervisorId)
        {
            throw new InvalidOperationException("Only the assigned supervisor can close an open shift.");
        }

        Status = ShiftStatus.Closed;
        ClosedAt = closedAt;
    }
}