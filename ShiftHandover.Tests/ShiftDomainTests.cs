using ShiftHandover.Web.Models;
using ShiftHandover.Web.Services;

namespace ShiftHandover.Tests;

public class ShiftDomainTests
{
    [Fact]
    public void BahrainDate_ChangesAtBahrainMidnightRatherThanUtcMidnight()
    {
        var beforeBahrainMidnight = new DateTimeOffset(2026, 10, 15, 20, 59, 0, TimeSpan.Zero);
        var atBahrainMidnight = new DateTimeOffset(2026, 10, 15, 21, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 15), BahrainTime.GetLocalDate(beforeBahrainMidnight));
        Assert.Equal(new DateOnly(2026, 10, 16), BahrainTime.GetLocalDate(atBahrainMidnight));
    }

    [Fact]
    public void Claim_AssignsAvailableShiftToSupervisor()
    {
        var shift = new Shift();
        var claimedAt = DateTimeOffset.UtcNow;

        shift.Claim("supervisor-1", claimedAt);

        Assert.Equal(ShiftStatus.Claimed, shift.Status);
        Assert.Equal("supervisor-1", shift.ClaimedBySupervisorId);
        Assert.Equal(claimedAt, shift.ClaimedAt);
        Assert.True(shift.CanBeEditedBy("supervisor-1"));
        Assert.False(shift.CanBeEditedBy("supervisor-2"));
    }

    [Fact]
    public void Claim_RejectsAlreadyClaimedShift()
    {
        var shift = new Shift();
        shift.Claim("supervisor-1", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() =>
            shift.Claim("supervisor-2", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Claim_RejectsClosedShift()
    {
        var shift = new Shift();
        shift.Claim("supervisor-1", DateTimeOffset.UtcNow);
        shift.Close("supervisor-1", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() =>
            shift.Claim("supervisor-2", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Close_RejectsOtherSupervisorAndLocksShiftAfterClose()
    {
        var shift = new Shift();
        shift.Claim("supervisor-1", DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() =>
            shift.Close("supervisor-2", DateTimeOffset.UtcNow));

        shift.Close("supervisor-1", DateTimeOffset.UtcNow);

        Assert.Equal(ShiftStatus.Closed, shift.Status);
        Assert.False(shift.CanBeEditedBy("supervisor-1"));
        Assert.Throws<InvalidOperationException>(() =>
            shift.Close("supervisor-1", DateTimeOffset.UtcNow));
    }
}