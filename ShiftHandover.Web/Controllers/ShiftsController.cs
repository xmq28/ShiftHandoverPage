using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Web.Controllers;

[Authorize(Roles = DevelopmentSupervisorSeeder.SupervisorRole)]
public class ShiftsController(ApplicationDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var shifts = await db.Shifts
            .AsNoTracking()
            .OrderBy(shift => shift.ShiftDate)
            .ThenBy(shift => shift.StartTime)
            .ToListAsync();

        return View(shifts);
    }

    public async Task<IActionResult> Details(int id)
    {
        var supervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var shift = await db.Shifts
            .AsNoTracking()
            .Include(item => item.ClaimedBySupervisor)
            .SingleOrDefaultAsync(item => item.Id == id);

        if (shift is null)
        {
            return NotFound();
        }

        if (shift.Status != ShiftStatus.Available && shift.ClaimedBySupervisorId != supervisorId)
        {
            return NotFound();
        }

        return View(shift);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(int id)
    {
        var supervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(supervisorId))
        {
            return Forbid();
        }

        var shift = await db.Shifts.SingleOrDefaultAsync(item => item.Id == id);
        if (shift is null)
        {
            return NotFound();
        }

        if (shift.Status != ShiftStatus.Available || shift.ClaimedBySupervisorId is not null)
        {
            TempData["Error"] = "That shift is no longer available to claim.";
            return RedirectToAction(nameof(Index));
        }

        shift.Claim(supervisorId, DateTimeOffset.UtcNow);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "Another supervisor claimed that shift first.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = "Shift claimed successfully.";
        return RedirectToAction(nameof(Details), new { id });
    }
}