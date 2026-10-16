using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Web.Controllers;

[Authorize(Roles = DevelopmentSupervisorSeeder.SupervisorRole)]
public class ShiftRecordsController(ApplicationDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Accidents(int id)
    {
        var shift = await FindOwnedOpenShiftAsync(id);
        if (shift is null)
        {
            return NotFound();
        }

        var records = await db.Accidents.AsNoTracking()
            .Where(record => record.ShiftId == id)
            .OrderByDescending(record => record.OccurredAt)
            .ToListAsync();

        return View(new AccidentLogViewModel
        {
            ShiftId = shift.Id,
            ShiftTitle = shift.Title,
            Records = records,
            Input = new AccidentInputModel()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accidents(int id, AccidentInputModel input)
    {
        var shift = await FindOwnedOpenShiftAsync(id);
        if (shift is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(await BuildAccidentViewModelAsync(shift, input));
        }

        db.Accidents.Add(new Accident
        {
            ShiftId = shift.Id,
            OccurredAt = input.OccurredAt!.Value,
            Location = input.Location.Trim(),
            Description = input.Description.Trim(),
            Severity = input.Severity,
            InjuredPerson = NullIfWhiteSpace(input.InjuredPerson),
            ActionTaken = NullIfWhiteSpace(input.ActionTaken),
            RecordedBySupervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!
        });
        await db.SaveChangesAsync();

        return RedirectToAction(nameof(Accidents), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Incidents(int id)
    {
        var shift = await FindOwnedOpenShiftAsync(id);
        if (shift is null)
        {
            return NotFound();
        }

        var records = await db.Incidents.AsNoTracking()
            .Where(record => record.ShiftId == id)
            .OrderByDescending(record => record.OccurredAt)
            .ToListAsync();

        return View(new IncidentLogViewModel
        {
            ShiftId = shift.Id,
            ShiftTitle = shift.Title,
            Records = records,
            Input = new IncidentInputModel()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Incidents(int id, IncidentInputModel input)
    {
        var shift = await FindOwnedOpenShiftAsync(id);
        if (shift is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(await BuildIncidentViewModelAsync(shift, input));
        }

        db.Incidents.Add(new Incident
        {
            ShiftId = shift.Id,
            OccurredAt = input.OccurredAt!.Value,
            Category = input.Category.Trim(),
            Description = input.Description.Trim(),
            Resolved = input.Resolved,
            FollowUpRequired = input.FollowUpRequired,
            RecordedBySupervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!
        });
        await db.SaveChangesAsync();

        return RedirectToAction(nameof(Incidents), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Manpower(int id)
    {
        var shift = await FindOwnedOpenShiftAsync(id);
        if (shift is null)
        {
            return NotFound();
        }

        var records = await db.ManpowerRecords.AsNoTracking()
            .Where(record => record.ShiftId == id)
            .OrderBy(record => record.Department)
            .ThenBy(record => record.Role)
            .ToListAsync();

        return View(new ManpowerLogViewModel
        {
            ShiftId = shift.Id,
            ShiftTitle = shift.Title,
            Records = records,
            Input = new ManpowerInputModel()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Manpower(int id, ManpowerInputModel input)
    {
        var shift = await FindOwnedOpenShiftAsync(id);
        if (shift is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(await BuildManpowerViewModelAsync(shift, input));
        }

        db.ManpowerRecords.Add(new ManpowerRecord
        {
            ShiftId = shift.Id,
            Department = input.Department.Trim(),
            Role = input.Role.Trim(),
            Required = input.Required,
            Present = input.Present,
            Absent = input.Absent,
            Notes = NullIfWhiteSpace(input.Notes),
            RecordedBySupervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!
        });
        await db.SaveChangesAsync();

        return RedirectToAction(nameof(Manpower), new { id });
    }

    private async Task<Shift?> FindOwnedOpenShiftAsync(int id)
    {
        var supervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(supervisorId))
        {
            return null;
        }

        return await db.Shifts.SingleOrDefaultAsync(shift =>
            shift.Id == id && shift.Status == ShiftStatus.Claimed && shift.ClaimedBySupervisorId == supervisorId);
    }

    private async Task<AccidentLogViewModel> BuildAccidentViewModelAsync(Shift shift, AccidentInputModel input) =>
        new()
        {
            ShiftId = shift.Id,
            ShiftTitle = shift.Title,
            Records = await db.Accidents.AsNoTracking()
                .Where(record => record.ShiftId == shift.Id)
                .OrderByDescending(record => record.OccurredAt)
                .ToListAsync(),
            Input = input
        };

    private async Task<IncidentLogViewModel> BuildIncidentViewModelAsync(Shift shift, IncidentInputModel input) =>
        new()
        {
            ShiftId = shift.Id,
            ShiftTitle = shift.Title,
            Records = await db.Incidents.AsNoTracking()
                .Where(record => record.ShiftId == shift.Id)
                .OrderByDescending(record => record.OccurredAt)
                .ToListAsync(),
            Input = input
        };

    private async Task<ManpowerLogViewModel> BuildManpowerViewModelAsync(Shift shift, ManpowerInputModel input) =>
        new()
        {
            ShiftId = shift.Id,
            ShiftTitle = shift.Title,
            Records = await db.ManpowerRecords.AsNoTracking()
                .Where(record => record.ShiftId == shift.Id)
                .OrderBy(record => record.Department)
                .ThenBy(record => record.Role)
                .ToListAsync(),
            Input = input
        };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}