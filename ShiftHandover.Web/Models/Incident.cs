namespace ShiftHandover.Web.Models;

public class Incident
{
    public int Id { get; set; }
    public int ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Resolved { get; set; }
    public bool FollowUpRequired { get; set; }
    public string RecordedBySupervisorId { get; set; } = string.Empty;
    public ApplicationUser RecordedBySupervisor { get; set; } = null!;
}