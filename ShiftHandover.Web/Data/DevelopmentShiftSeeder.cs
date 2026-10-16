using Microsoft.EntityFrameworkCore;

namespace ShiftHandover.Web.Data;

public static class DevelopmentShiftSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db)
    {
        if (await db.Shifts.AnyAsync())
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        db.Shifts.AddRange(
            new Models.Shift
            {
                Title = "Morning Operations",
                ShiftDate = today,
                StartTime = new TimeOnly(6, 0),
                EndTime = new TimeOnly(14, 0)
            },
            new Models.Shift
            {
                Title = "Evening Operations",
                ShiftDate = today,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(22, 0)
            },
            new Models.Shift
            {
                Title = "Overnight Operations",
                ShiftDate = today.AddDays(1),
                StartTime = new TimeOnly(22, 0),
                EndTime = new TimeOnly(6, 0)
            });

        await db.SaveChangesAsync();
    }
}