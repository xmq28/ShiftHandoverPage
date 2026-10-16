namespace ShiftHandover.Web.Models;

public class ShiftReportDelivery
{
    public int Id { get; set; }
    public int ShiftId { get; set; }
    public Shift Shift { get; set; } = null!;
    public ReportDeliveryStatus Status { get; set; } = ReportDeliveryStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? FailureReason { get; set; }
}