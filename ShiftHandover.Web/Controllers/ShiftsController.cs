using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;
using ShiftHandover.Web.Services;

namespace ShiftHandover.Web.Controllers;

[Authorize(Roles = DevelopmentSupervisorSeeder.SupervisorRole)]
public class ShiftsController(ApplicationDbContext db, IShiftReportDeliveryService reportDelivery) : Controller
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
        var shift = await db.Shifts
            .AsNoTracking()
            .Include(item => item.ClaimedBySupervisor)
            .Include(item => item.ReportDelivery)
            .SingleOrDefaultAsync(item => item.Id == id);

        if (shift is null)
        {
            return NotFound();
        }

        return View(shift);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id)
    {
        var supervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(supervisorId))
        {
            return Forbid();
        }

        var shift = await db.Shifts.SingleOrDefaultAsync(item =>
            item.Id == id && item.Status == ShiftStatus.Claimed && item.ClaimedBySupervisorId == supervisorId);
        if (shift is null)
        {
            return NotFound();
        }

        shift.Close(supervisorId, DateTimeOffset.UtcNow);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = "The shift changed before it could be closed.";
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["Success"] = "Shift closed successfully.";
        var delivery = await reportDelivery.DeliverAsync(id, supervisorId);
        if (delivery.Status == ReportDeliveryStatus.Sent)
        {
            TempData["Success"] = delivery.WasAlreadyProcessed
                ? "Shift closed. Its report was already sent; no duplicate email was sent."
                : $"Shift closed and report emailed to {delivery.RecipientCount} other supervisor(s).";
        }
        else
        {
            TempData["Error"] = $"Shift closed, but report delivery failed: {delivery.FailureReason ?? "Delivery is already pending."}";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Report(int id)
    {
        try
        {
            var pdf = await reportDelivery.CreatePdfAsync(id);
            return pdf is null
                ? NotFound()
                : File(pdf, "application/pdf", $"shift-{id}-handover.pdf");
        }
        catch (Exception exception)
        {
            TempData["Error"] = $"The shift report PDF could not be generated: {exception.Message}";
            return RedirectToAction(nameof(Details), new { id });
        }
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