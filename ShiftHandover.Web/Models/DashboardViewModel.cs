namespace ShiftHandover.Web.Models;

public sealed class DashboardViewModel
{
    public required string SupervisorName { get; init; }
    public required DateTimeOffset BahrainNow { get; init; }
    public required int TotalShifts { get; init; }
    public required int AvailableShifts { get; init; }
    public required int InProgressShifts { get; init; }
    public required int ClosedShifts { get; init; }
    public required IReadOnlyList<DashboardShiftViewModel> RecentShifts { get; init; }
    public required IReadOnlyList<DashboardDeliveryViewModel> RecentDeliveries { get; init; }
}

public sealed class DashboardShiftViewModel
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required DateOnly ShiftDate { get; init; }
    public required TimeOnly StartTime { get; init; }
    public required TimeOnly EndTime { get; init; }
    public required ShiftStatus Status { get; init; }
    public string? SupervisorName { get; init; }
    public required bool IsAssignedToCurrentSupervisor { get; init; }
}

public sealed class DashboardDeliveryViewModel
{
    public required int ShiftId { get; init; }
    public required string ShiftTitle { get; init; }
    public required ReportDeliveryStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? FailureReason { get; init; }
}
