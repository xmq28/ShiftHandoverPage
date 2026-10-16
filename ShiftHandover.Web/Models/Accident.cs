namespace ShiftHandover.Web.Models;

public class Accident
{
    public int Id { get; set; }
    public int ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AccidentSeverity Severity { get; set; }
    public string? InjuredPerson { get; set; }
    public string? ActionTaken { get; set; }
    public string RecordedBySupervisorId { get; set; } = string.Empty;
    public ApplicationUser RecordedBySupervisor { get; set; } = null!;
}