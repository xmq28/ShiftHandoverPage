namespace ShiftHandover.Web.Models;

public class ManpowerRecord
{
    public int Id { get; set; }
    public int ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public string Department { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int Required { get; set; }
    public int Present { get; set; }
    public int Absent { get; set; }
    public string? Notes { get; set; }
    public string RecordedBySupervisorId { get; set; } = string.Empty;
    public ApplicationUser RecordedBySupervisor { get; set; } = null!;
}