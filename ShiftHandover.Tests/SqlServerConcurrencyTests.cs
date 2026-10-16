using Microsoft.EntityFrameworkCore;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Tests;

public class SqlServerConcurrencyTests
{
    [Fact]
    public async Task ConcurrentClaims_OnlyOneSupervisorCanSave()
    {
        var databaseName = $"ShiftHandoverConcurrency_{Guid.NewGuid():N}";
        var connectionString = $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var shiftId = 0;

        try
        {
            await using (var setupContext = new ApplicationDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync();
                setupContext.Users.AddRange(
                    new ApplicationUser { Id = "supervisor-1", UserName = "supervisor-1", FullName = "Supervisor One" },
                    new ApplicationUser { Id = "supervisor-2", UserName = "supervisor-2", FullName = "Supervisor Two" });
                var shift = new Shift
                {
                    Title = "Night Shift",
                    ShiftDate = new DateOnly(2026, 10, 16),
                    StartTime = new TimeOnly(22, 0),
                    EndTime = new TimeOnly(6, 0)
                };
                await setupContext.SaveChangesAsync();
                setupContext.Shifts.Add(shift);
                await setupContext.SaveChangesAsync();
                shiftId = shift.Id;
            }

            await using (var firstContext = new ApplicationDbContext(options))
            await using (var secondContext = new ApplicationDbContext(options))
            {
                var firstShift = await firstContext.Shifts.SingleAsync(shift => shift.Id == shiftId);
                var secondShift = await secondContext.Shifts.SingleAsync(shift => shift.Id == shiftId);
                var claimTime = DateTimeOffset.UtcNow;

                firstShift.Claim("supervisor-1", claimTime);
                secondShift.Claim("supervisor-2", claimTime);

                await firstContext.SaveChangesAsync();
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => secondContext.SaveChangesAsync());
            }

            await using var verifyContext = new ApplicationDbContext(options);
            var savedShift = await verifyContext.Shifts.SingleAsync(shift => shift.Id == shiftId);
            Assert.Equal("supervisor-1", savedShift.ClaimedBySupervisorId);
        }
        finally
        {
            await using var cleanupContext = new ApplicationDbContext(options);
            await cleanupContext.Database.EnsureDeletedAsync();
        }
    }
}