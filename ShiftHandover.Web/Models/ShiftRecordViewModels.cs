using System.ComponentModel.DataAnnotations;

namespace ShiftHandover.Web.Models;

public sealed class AccidentInputModel
{
    [Required]
    public DateTimeOffset? OccurredAt { get; set; }

    [Required, StringLength(160)]
    public string Location { get; set; } = string.Empty;

    [Required, StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    [EnumDataType(typeof(AccidentSeverity))]
    public AccidentSeverity Severity { get; set; }

    [StringLength(160)]
    public string? InjuredPerson { get; set; }

    [StringLength(2000)]
    public string? ActionTaken { get; set; }
}

public sealed class IncidentInputModel
{
    [Required]
    public DateTimeOffset? OccurredAt { get; set; }

    [Required, StringLength(120)]
    public string Category { get; set; } = string.Empty;

    [Required, StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    public bool Resolved { get; set; }
    public bool FollowUpRequired { get; set; }
}

public sealed class ManpowerInputModel
{
    [Required, StringLength(120)]
    public string Department { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string Role { get; set; } = string.Empty;

    [Range(0, int.MaxValue)]
    public int Required { get; set; }

    [Range(0, int.MaxValue)]
    public int Present { get; set; }

    [Range(0, int.MaxValue)]
    public int Absent { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

public sealed class AccidentLogViewModel
{
    public required int ShiftId { get; init; }
    public required string ShiftTitle { get; init; }
    public required bool CanEdit { get; init; }
    public required IReadOnlyList<Accident> Records { get; init; }
    public required AccidentInputModel Input { get; init; }
}

public sealed class IncidentLogViewModel
{
    public required int ShiftId { get; init; }
    public required string ShiftTitle { get; init; }
    public required bool CanEdit { get; init; }
    public required IReadOnlyList<Incident> Records { get; init; }
    public required IncidentInputModel Input { get; init; }
}

public sealed class ManpowerLogViewModel
{
    public required int ShiftId { get; init; }
    public required string ShiftTitle { get; init; }
    public required bool CanEdit { get; init; }
    public required IReadOnlyList<ManpowerRecord> Records { get; init; }
    public required ManpowerInputModel Input { get; init; }
}