using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Web.Services;

public sealed record ShiftReportDeliveryResult(
    ReportDeliveryStatus Status,
    int RecipientCount,
    string? FailureReason,
    bool WasAlreadyProcessed);

public interface IShiftReportDeliveryService
{
    Task<byte[]?> CreatePdfAsync(int shiftId);
    Task<ShiftReportDeliveryResult> DeliverAsync(int shiftId, string closedBySupervisorId);
}

public sealed class ShiftReportDeliveryService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> users,
    IShiftReportPdfGenerator pdfGenerator,
    IShiftReportEmailSender emailSender) : IShiftReportDeliveryService
{
    public async Task<byte[]?> CreatePdfAsync(int shiftId)
    {
        var report = await LoadClosedShiftAsync(shiftId);
        return report is null ? null : Generate(report);
    }

    public async Task<ShiftReportDeliveryResult> DeliverAsync(int shiftId, string closedBySupervisorId)
    {
        var existing = await db.ShiftReportDeliveries.SingleOrDefaultAsync(item => item.ShiftId == shiftId);
        if (existing is not null)
        {
            return Result(existing, wasAlreadyProcessed: true);
        }

        var report = await LoadClosedShiftAsync(shiftId)
            ?? throw new InvalidOperationException("A report can only be delivered for a closed shift.");
        var delivery = new ShiftReportDelivery
        {
            ShiftId = shiftId,
            Status = ReportDeliveryStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ShiftReportDeliveries.Add(delivery);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.Entry(delivery).State = EntityState.Detached;
            var concurrentDelivery = await db.ShiftReportDeliveries.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ShiftId == shiftId);
            if (concurrentDelivery is null)
            {
                throw;
            }

            return Result(concurrentDelivery, wasAlreadyProcessed: true);
        }

        byte[] pdf;
        try
        {
            pdf = Generate(report);
        }
        catch (Exception exception)
        {
            return await FailAsync(delivery, $"PDF generation failed: {exception.Message}");
        }

        var supervisors = await users.GetUsersInRoleAsync(DevelopmentSupervisorSeeder.SupervisorRole);
        var recipients = supervisors
            .Where(supervisor => supervisor.Id != closedBySupervisorId)
            .Select(supervisor => supervisor.Email)
            .Where(IsDeliverableRecipient)
            .Select(email => email!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (recipients.Length == 0)
        {
            return await FailAsync(delivery, "Email delivery failed: no other supervisor has a valid, non-test email address.");
        }

        try
        {
            await emailSender.SendAsync(recipients, $"shift-{shiftId}-handover.pdf", pdf);
        }
        catch (Exception exception)
        {
            return await FailAsync(delivery, $"Email delivery failed: {exception.Message}");
        }

        delivery.Status = ReportDeliveryStatus.Sent;
        delivery.SentAt = DateTimeOffset.UtcNow;
        delivery.FailureReason = null;
        await db.SaveChangesAsync();
        return new ShiftReportDeliveryResult(delivery.Status, recipients.Length, null, false);
    }

    private async Task<ShiftReportDeliveryResult> FailAsync(ShiftReportDelivery delivery, string reason)
    {
        delivery.Status = ReportDeliveryStatus.Failed;
        delivery.FailureReason = reason.Length <= 1000 ? reason : reason[..1000];
        await db.SaveChangesAsync();
        return new ShiftReportDeliveryResult(delivery.Status, 0, delivery.FailureReason, false);
    }

    private async Task<ShiftReportSnapshot?> LoadClosedShiftAsync(int shiftId)
    {
        var shift = await db.Shifts.AsNoTracking()
            .Include(item => item.ClaimedBySupervisor)
            .SingleOrDefaultAsync(item => item.Id == shiftId && item.Status == ShiftStatus.Closed);
        if (shift is null)
        {
            return null;
        }

        var accidents = await db.Accidents.AsNoTracking().Include(item => item.RecordedBySupervisor)
            .Where(item => item.ShiftId == shiftId).ToListAsync();
        var incidents = await db.Incidents.AsNoTracking().Include(item => item.RecordedBySupervisor)
            .Where(item => item.ShiftId == shiftId).ToListAsync();
        var manpower = await db.ManpowerRecords.AsNoTracking().Include(item => item.RecordedBySupervisor)
            .Where(item => item.ShiftId == shiftId).ToListAsync();
        return new ShiftReportSnapshot(shift, accidents, incidents, manpower);
    }

    private byte[] Generate(ShiftReportSnapshot report) => pdfGenerator.Generate(
        report.Shift,
        report.Accidents,
        report.Incidents,
        report.ManpowerRecords);

    private static bool IsDeliverableRecipient(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !MailboxAddress.TryParse(email, out var mailbox))
        {
            return false;
        }

        var domain = mailbox.Address[(mailbox.Address.LastIndexOf('@') + 1)..];
        return !new[] { ".test", ".example", ".invalid", ".localhost" }
            .Any(suffix => domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static ShiftReportDeliveryResult Result(ShiftReportDelivery delivery, bool wasAlreadyProcessed) =>
        new(delivery.Status, 0, delivery.FailureReason, wasAlreadyProcessed);

    private sealed record ShiftReportSnapshot(
        Shift Shift,
        IReadOnlyCollection<Accident> Accidents,
        IReadOnlyCollection<Incident> Incidents,
        IReadOnlyCollection<ManpowerRecord> ManpowerRecords);
}
