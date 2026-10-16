using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;
using ShiftHandover.Web.Services;

namespace ShiftHandover.Web.Controllers;

[Authorize(Roles = DevelopmentSupervisorSeeder.SupervisorRole)]
public class HomeController(ApplicationDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var supervisorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(supervisorId))
        {
            return Forbid();
        }

        var supervisorName = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == supervisorId)
            .Select(user => user.FullName)
            .SingleOrDefaultAsync();

        var recentShifts = await db.Shifts
            .AsNoTracking()
            .Include(shift => shift.ClaimedBySupervisor)
            .OrderByDescending(shift => shift.ShiftDate)
            .ThenByDescending(shift => shift.StartTime)
            .Take(6)
            .ToListAsync();

        var recentDeliveries = await db.ShiftReportDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Shift)
            .OrderByDescending(delivery => delivery.CreatedAt)
            .Take(4)
            .ToListAsync();

        var model = new DashboardViewModel
        {
            SupervisorName = string.IsNullOrWhiteSpace(supervisorName) ? User.Identity?.Name ?? "Supervisor" : supervisorName,
            BahrainNow = DateTimeOffset.UtcNow.ToOffset(BahrainTime.UtcOffset),
            TotalShifts = await db.Shifts.CountAsync(),
            AvailableShifts = await db.Shifts.CountAsync(shift => shift.Status == ShiftStatus.Available),
            InProgressShifts = await db.Shifts.CountAsync(shift => shift.Status == ShiftStatus.Claimed),
            ClosedShifts = await db.Shifts.CountAsync(shift => shift.Status == ShiftStatus.Closed),
            RecentShifts = recentShifts.Select(shift => new DashboardShiftViewModel
            {
                Id = shift.Id,
                Title = shift.Title,
                ShiftDate = shift.ShiftDate,
                StartTime = shift.StartTime,
                EndTime = shift.EndTime,
                Status = shift.Status,
                SupervisorName = shift.ClaimedBySupervisor?.FullName,
                IsAssignedToCurrentSupervisor = shift.ClaimedBySupervisorId == supervisorId
            }).ToList(),
            RecentDeliveries = recentDeliveries.Select(delivery => new DashboardDeliveryViewModel
            {
                ShiftId = delivery.ShiftId,
                ShiftTitle = delivery.Shift.Title,
                Status = delivery.Status,
                CreatedAt = delivery.CreatedAt,
                SentAt = delivery.SentAt,
                FailureReason = delivery.FailureReason
            }).ToList()
        };

        return View(model);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
