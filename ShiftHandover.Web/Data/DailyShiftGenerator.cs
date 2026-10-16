using Microsoft.EntityFrameworkCore;

namespace ShiftHandover.Web.Data;

public static class DailyShiftGenerator
{
    public static async Task EnsureShiftsForDateAsync(
        ApplicationDbContext db,
        DateOnly bahrainDate,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "EXEC sp_getapplock @Resource = 'ShiftHandover.DailyShiftGeneration', @LockMode = 'Exclusive', @LockOwner = 'Transaction';",
            cancellationToken);

        var scheduledShifts = new[]
        {
            (Title: "Morning Operations", Date: bahrainDate, Start: new TimeOnly(6, 0), End: new TimeOnly(14, 0)),
            (Title: "Evening Operations", Date: bahrainDate, Start: new TimeOnly(14, 0), End: new TimeOnly(22, 0)),
            (Title: "Overnight Operations", Date: bahrainDate.AddDays(1), Start: new TimeOnly(22, 0), End: new TimeOnly(6, 0))
        };
        var shiftDates = scheduledShifts.Select(shift => shift.Date).Distinct().ToArray();
        var shiftTitles = scheduledShifts.Select(shift => shift.Title).Distinct().ToArray();
        var existingShifts = await db.Shifts
            .Where(shift => shiftDates.Contains(shift.ShiftDate) && shiftTitles.Contains(shift.Title))
            .Select(shift => new { shift.Title, shift.ShiftDate, shift.StartTime, shift.EndTime })
            .ToListAsync(cancellationToken);

        foreach (var scheduledShift in scheduledShifts)
        {
            if (existingShifts.Any(existing =>
                    existing.Title == scheduledShift.Title &&
                    existing.ShiftDate == scheduledShift.Date &&
                    existing.StartTime == scheduledShift.Start &&
                    existing.EndTime == scheduledShift.End))
            {
                continue;
            }

            db.Shifts.Add(new Models.Shift
            {
                Title = scheduledShift.Title,
                ShiftDate = scheduledShift.Date,
                StartTime = scheduledShift.Start,
                EndTime = scheduledShift.End
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}