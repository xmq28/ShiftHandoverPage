using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;
using ShiftHandover.Web.Services;
using UglyToad.PdfPig;

namespace ShiftHandover.Tests;

public class ShiftReportDeliveryTests : IClassFixture<ShiftReportDeliveryTests.Factory>
{
    private const string Password = "Test-Password-123!";
    private const string OwnerEmail = "report-owner@company.com";
    private const string OtherSupervisorEmail = "report-other@company.com";
    private const string OwnerId = "report-owner-id";
    private const string OtherSupervisorId = "report-other-id";
    private readonly Factory _factory;

    public ShiftReportDeliveryTests(Factory factory)
    {
        _factory = factory;
        _factory.EmailSender.Reset();
        _factory.PdfGenerator.Failure = null;
    }

    public sealed class Factory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database=ShiftReportDelivery_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

        public RecordingEmailSender EmailSender { get; } = new();
        public ControllablePdfGenerator PdfGenerator { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(_connectionString));
                services.RemoveAll<IShiftReportEmailSender>();
                services.AddSingleton<IShiftReportEmailSender>(EmailSender);
                services.RemoveAll<IShiftReportPdfGenerator>();
                services.AddSingleton<IShiftReportPdfGenerator>(PdfGenerator);
            });
        }

        public async Task InitializeAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();

            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var roleResult = await roles.CreateAsync(new IdentityRole(DevelopmentSupervisorSeeder.SupervisorRole));
            Assert.True(roleResult.Succeeded);

            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await CreateUserAsync(users, OwnerEmail, OwnerId, isSupervisor: true);
            await CreateUserAsync(users, OtherSupervisorEmail, OtherSupervisorId, isSupervisor: true);
            await CreateUserAsync(users, "report-test-domain@example.test", "report-test-domain-id", isSupervisor: true);
            await CreateUserAsync(users, "report-regular@example.test", "report-regular-id", isSupervisor: false);
        }

        public new async Task DisposeAsync()
        {
            using (var scope = Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureDeletedAsync();
            }

            await base.DisposeAsync();
        }

        private static async Task CreateUserAsync(UserManager<ApplicationUser> users, string email, string id, bool isSupervisor)
        {
            var result = await users.CreateAsync(new ApplicationUser
            {
                Id = id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = id switch
                {
                    OwnerId => "Owner Supervisor",
                    OtherSupervisorId => "Other Supervisor",
                    _ => "Regular User"
                }
            }, Password);
            Assert.True(result.Succeeded);

            if (isSupervisor)
            {
                var user = await users.FindByIdAsync(id) ?? throw new InvalidOperationException("Test user was not created.");
                var roleResult = await users.AddToRoleAsync(user, DevelopmentSupervisorSeeder.SupervisorRole);
                Assert.True(roleResult.Succeeded);
            }
        }
    }

    public sealed class RecordingEmailSender : IShiftReportEmailSender
    {
        public int Attempts { get; private set; }
        public IReadOnlyCollection<string> LastRecipients { get; private set; } = [];
        public string? LastFileName { get; private set; }
        public byte[]? LastPdf { get; private set; }
        public Exception? Failure { get; set; }

        public Task SendAsync(IReadOnlyCollection<string> recipients, string fileName, byte[] pdf)
        {
            Attempts++;
            LastRecipients = recipients.ToArray();
            LastFileName = fileName;
            LastPdf = pdf.ToArray();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.CompletedTask;
        }

        public void Reset()
        {
            Attempts = 0;
            LastRecipients = [];
            LastFileName = null;
            LastPdf = null;
            Failure = null;
        }
    }

    public sealed class ControllablePdfGenerator : IShiftReportPdfGenerator
    {
        private readonly ShiftReportPdfGenerator _actualGenerator = new();
        public Exception? Failure { get; set; }

        public byte[] Generate(
            Shift shift,
            IReadOnlyCollection<Accident> accidents,
            IReadOnlyCollection<Incident> incidents,
            IReadOnlyCollection<ManpowerRecord> manpowerRecords)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return _actualGenerator.Generate(shift, accidents, incidents, manpowerRecords);
        }
    }

    [Fact]
    public async Task DownloadedPdf_ContainsShiftDetailsAndEveryRecordType()
    {
        var shiftId = await CreateClosedShiftAsync("Report shift title marker");
        using var client = await CreateAuthenticatedClientAsync(OtherSupervisorEmail);

        var response = await client.GetAsync($"/Shifts/Report/{shiftId}");

        if (response.StatusCode != HttpStatusCode.OK && response.Headers.Location is { } location)
        {
            var details = await client.GetStringAsync(location);
            var error = Regex.Match(details, "The shift report PDF could not be generated: ([^<]+)");
            Assert.Fail(error.Success ? error.Groups[1].Value : details);
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"shift-{shiftId}-handover.pdf", response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(pdfBytes.Length > 100);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5));

        var text = ExtractText(pdfBytes);
        Assert.Contains("Report shift title marker", text);
        Assert.Contains("2026", text);
        Assert.Contains("Report accident location marker", text);
        Assert.Contains("High", text);
        Assert.Contains("Report accident description marker", text);
        Assert.Contains("Report injured person marker", text);
        Assert.Contains("Report action marker", text);
        Assert.Contains("Owner Supervisor", text);
        Assert.Contains("Report incident category marker", text);
        Assert.Contains("Report incident description marker", text);
        Assert.Contains("Follow-up required", text);
        Assert.Contains("Report manpower department marker", text);
        Assert.Contains("Report manpower role marker", text);
        Assert.Contains("Report manpower notes marker", text);
        Assert.Contains("Required", text);
        Assert.Contains("Present", text);
        Assert.Contains("Absent", text);
        Assert.Contains("13:15", text);
        Assert.Contains("Bahrain", text);
        Assert.DoesNotContain(OwnerId, text);
        using var document = PdfDocument.Open(new MemoryStream(pdfBytes));
        var incidentPage = document.GetPages()
            .Select(page => string.Join(' ', page.GetWords().Select(word => word.Text)))
            .Single(pageText => pageText.Contains("Report incident category marker", StringComparison.Ordinal));
        Assert.Contains("Report incident description marker", incidentPage);
        Assert.Contains("Follow-up required", incidentPage);
        Assert.Contains("Recorded by", incidentPage);
        Assert.Contains("Owner Supervisor", incidentPage);
    }

    [Fact]
    public async Task Close_SendsPdfToOtherSupervisorsOnceAndPersistsSentStatus()
    {
        var shiftId = await CreateClaimedShiftAsync("Email delivery report shift");
        using var owner = await CreateAuthenticatedClientAsync(OwnerEmail);
        var token = await ReadAntiforgeryTokenAsync(owner, $"/Shifts/Details/{shiftId}");

        var response = await CloseAsync(owner, shiftId, token);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, _factory.EmailSender.Attempts);
        Assert.Equal([OtherSupervisorEmail], _factory.EmailSender.LastRecipients);
        Assert.DoesNotContain(_factory.EmailSender.LastRecipients, email => email.EndsWith(".test", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"shift-{shiftId}-handover.pdf", _factory.EmailSender.LastFileName);
        Assert.NotNull(_factory.EmailSender.LastPdf);
        var attachedText = ExtractText(_factory.EmailSender.LastPdf!);
        Assert.Contains("Email delivery report shift", attachedText);
        Assert.Contains("Report accident description marker", attachedText);
        Assert.Contains("Owner Supervisor", attachedText);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(_factory.EmailSender.LastPdf!, 0, 5));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var delivery = await db.ShiftReportDeliveries.SingleAsync(item => item.ShiftId == shiftId);
        Assert.Equal(ReportDeliveryStatus.Sent, delivery.Status);
        Assert.NotNull(delivery.SentAt);

        var retryResponse = await CloseAsync(owner, shiftId, token);
        Assert.Equal(HttpStatusCode.NotFound, retryResponse.StatusCode);
        var retryResult = await scope.ServiceProvider.GetRequiredService<IShiftReportDeliveryService>()
            .DeliverAsync(shiftId, OwnerId);
        Assert.True(retryResult.WasAlreadyProcessed);
        Assert.Equal(ReportDeliveryStatus.Sent, retryResult.Status);
        Assert.Equal(1, _factory.EmailSender.Attempts);
        Assert.Single(await db.ShiftReportDeliveries.Where(item => item.ShiftId == shiftId).ToListAsync());
    }

    [Fact]
    public async Task PdfGenerationFailure_LeavesShiftClosedAndRecordsClearFailure()
    {
        var shiftId = await CreateClaimedShiftAsync("PDF failure report shift");
        _factory.PdfGenerator.Failure = new InvalidOperationException("PDF engine unavailable");
        using var owner = await CreateAuthenticatedClientAsync(OwnerEmail);
        var token = await ReadAntiforgeryTokenAsync(owner, $"/Shifts/Details/{shiftId}");

        await CloseAsync(owner, shiftId, token);
        var details = await owner.GetStringAsync($"/Shifts/Details/{shiftId}");

        Assert.Contains("PDF generation failed: PDF engine unavailable", details);
        Assert.Equal(ShiftStatus.Closed, await ReadShiftStatusAsync(shiftId));
        Assert.Equal(0, _factory.EmailSender.Attempts);
        Assert.Equal(ReportDeliveryStatus.Failed, await ReadDeliveryStatusAsync(shiftId));
    }

    [Fact]
    public async Task EmailFailure_IsPersistedAndNotAutomaticallyRetried()
    {
        var shiftId = await CreateClaimedShiftAsync("SMTP failure report shift");
        _factory.EmailSender.Failure = new InvalidOperationException("SMTP unavailable");
        using var owner = await CreateAuthenticatedClientAsync(OwnerEmail);
        var token = await ReadAntiforgeryTokenAsync(owner, $"/Shifts/Details/{shiftId}");

        await CloseAsync(owner, shiftId, token);
        var details = await owner.GetStringAsync($"/Shifts/Details/{shiftId}");

        Assert.Contains("Email delivery failed: SMTP unavailable", details);
        Assert.Equal(ReportDeliveryStatus.Failed, await ReadDeliveryStatusAsync(shiftId));
        var downloadableReport = await owner.GetAsync($"/Shifts/Report/{shiftId}");
        Assert.Equal(HttpStatusCode.OK, downloadableReport.StatusCode);
        Assert.Equal("application/pdf", downloadableReport.Content.Headers.ContentType?.MediaType);
        var pdf = await downloadableReport.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        using var scope = _factory.Services.CreateScope();
        var retryResult = await scope.ServiceProvider.GetRequiredService<IShiftReportDeliveryService>()
            .DeliverAsync(shiftId, OwnerId);
        Assert.True(retryResult.WasAlreadyProcessed);
        Assert.Equal(1, _factory.EmailSender.Attempts);
    }

    [Fact]
    public async Task NoRealOtherSupervisorEmail_FailsWithoutAttemptingSend()
    {
        var shiftId = await CreateClaimedShiftAsync("Test-domain recipient shift");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var shift = await db.Shifts.SingleAsync(item => item.Id == shiftId);
        shift.Close(OwnerId, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var otherSupervisor = await users.FindByIdAsync(OtherSupervisorId);
        Assert.NotNull(otherSupervisor);
        var originalEmail = otherSupervisor.Email;
        var updateResult = await users.SetEmailAsync(otherSupervisor, "report-other@example.test");
        Assert.True(updateResult.Succeeded);

        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<IShiftReportDeliveryService>()
                .DeliverAsync(shiftId, OwnerId);

            Assert.Equal(ReportDeliveryStatus.Failed, result.Status);
            Assert.Contains("no other supervisor has a valid, non-test email address", result.FailureReason);
            Assert.Equal(0, _factory.EmailSender.Attempts);
        }
        finally
        {
            otherSupervisor = await users.FindByIdAsync(OtherSupervisorId);
            Assert.NotNull(otherSupervisor);
            var restoreResult = await users.SetEmailAsync(otherSupervisor, originalEmail);
            Assert.True(restoreResult.Succeeded);
        }
    }

    [Fact]
    public async Task MissingSmtpSettings_FailWithNamesOfRequiredConfigurationKeys()
    {
        var sender = new SmtpShiftReportEmailSender(Options.Create(new SmtpOptions()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync([OtherSupervisorEmail], "report.pdf", [1, 2, 3]));

        Assert.Contains("Email:Smtp:Host", exception.Message);
        Assert.Contains("Email:Smtp:UserName", exception.Message);
        Assert.Contains("Email:Smtp:Password", exception.Message);
        Assert.Contains("Email:Smtp:FromAddress", exception.Message);
    }

    [Fact]
    public async Task DevelopmentSeeder_UpdatesExistingEmailWithoutReplacingSupervisorIdentity()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var originalEmail = await users.FindByIdAsync(OwnerId);
        Assert.NotNull(originalEmail);
        var updatedEmail = "report-owner-updated@example.test";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SeedSupervisors:Supervisor1:UserId"] = OwnerId,
            ["SeedSupervisors:Supervisor1:Email"] = updatedEmail
        }).Build();

        try
        {
            await DevelopmentSupervisorSeeder.SeedAsync(scope.ServiceProvider, configuration);

            var updatedUser = await users.FindByIdAsync(OwnerId);
            Assert.NotNull(updatedUser);
            Assert.Equal(updatedEmail, updatedUser.Email);
            Assert.True(await users.IsInRoleAsync(updatedUser, DevelopmentSupervisorSeeder.SupervisorRole));
        }
        finally
        {
            var user = await users.FindByIdAsync(OwnerId);
            if (user is not null && user.Email != OwnerEmail)
            {
                var result = await users.SetEmailAsync(user, OwnerEmail);
                Assert.True(result.Succeeded);
            }
        }
    }

    [Fact]
    public void Pdf_LongNarrativeRowsRepeatHeadersAndIncludePageNumbers()
    {
        var shift = new Shift
        {
            Title = "Pagination test shift",
            ShiftDate = new DateOnly(2026, 10, 16),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0)
        };
        shift.Claim(OwnerId, new DateTimeOffset(2026, 10, 16, 7, 0, 0, TimeSpan.FromHours(3)));
        shift.Close(OwnerId, new DateTimeOffset(2026, 10, 16, 16, 0, 0, TimeSpan.FromHours(3)));
        var incidents = Enumerable.Range(0, 4).Select(index => new Incident
        {
            Id = index + 1,
            OccurredAt = new DateTimeOffset(2026, 10, 16, 10, index, 0, TimeSpan.FromHours(3)),
            Category = $"Long incident {index}",
            Description = string.Join(" ", Enumerable.Repeat("A detailed incident narrative for pagination", 85)),
            RecordedBySupervisor = new ApplicationUser { FullName = "Owner Supervisor" }
        }).ToArray();

        var pdf = new ShiftReportPdfGenerator().Generate(shift, [], incidents, []);
        using var document = PdfDocument.Open(new MemoryStream(pdf));

        Assert.True(document.NumberOfPages > 1);
        for (var index = 0; index < document.NumberOfPages; index++)
        {
            var pageText = string.Join(' ', document.GetPage(index + 1).GetWords().Select(word => word.Text));
            Assert.Contains($"Page {index + 1} of {document.NumberOfPages}", pageText);
            Assert.Contains("Field", pageText);
            Assert.Contains("Details", pageText);
        }
    }

    [Fact]
    public async Task IncidentDateTimeInput_IsStoredAndRenderedAsBahrainLocalTime()
    {
        var shiftId = await CreateClaimedShiftAsync("Bahrain time input shift");
        using var owner = await CreateAuthenticatedClientAsync(OwnerEmail);
        var token = await ReadAntiforgeryTokenAsync(owner, $"/ShiftRecords/Incidents/{shiftId}");

        var response = await owner.PostAsync($"/ShiftRecords/Incidents/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.OccurredAt"] = "2026-10-16T23:30",
            ["Input.Category"] = "Bahrain time marker",
            ["Input.Description"] = "Bahrain-local input marker",
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Incidents.SingleAsync(record => record.Category == "Bahrain time marker");
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 23, 30, 0, TimeSpan.FromHours(3)), stored.OccurredAt);

        var page = await owner.GetStringAsync($"/ShiftRecords/Incidents/{shiftId}");
        Assert.Contains("16 Oct 2026, 23:30 Bahrain (UTC+03:00)", WebUtility.HtmlDecode(page));
    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost")
    });

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = CreateClient();
        var token = await ReadAntiforgeryTokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private async Task<int> CreateClaimedShiftAsync(string title) => await CreateShiftAsync(title, close: false);

    private async Task<int> CreateClosedShiftAsync(string title) => await CreateShiftAsync(title, close: true);

    private async Task<int> CreateShiftAsync(string title, bool close)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var shift = new Shift
        {
            Title = title,
            ShiftDate = new DateOnly(2026, 10, 16),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0)
        };
        shift.Claim(OwnerId, DateTimeOffset.UtcNow);
        if (close)
        {
            shift.Close(OwnerId, DateTimeOffset.UtcNow);
        }

        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        db.Accidents.Add(new Accident
        {
            ShiftId = shift.Id,
            OccurredAt = new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero),
            Location = "Report accident location marker",
            Description = "Report accident description marker",
            Severity = AccidentSeverity.High,
            InjuredPerson = "Report injured person marker",
            ActionTaken = "Report action marker",
            RecordedBySupervisorId = OwnerId
        });
        db.Incidents.Add(new Incident
        {
            ShiftId = shift.Id,
            OccurredAt = new DateTimeOffset(2026, 10, 16, 10, 15, 0, TimeSpan.Zero),
            Category = "Report incident category marker",
            Description = "Report incident description marker",
            Resolved = true,
            FollowUpRequired = true,
            RecordedBySupervisorId = OwnerId
        });
        db.ManpowerRecords.Add(new ManpowerRecord
        {
            ShiftId = shift.Id,
            Department = "Report manpower department marker",
            Role = "Report manpower role marker",
            Required = 5,
            Present = 4,
            Absent = 1,
            Notes = "Report manpower notes marker",
            RecordedBySupervisorId = OwnerId
        });
        await db.SaveChangesAsync();
        return shift.Id;
    }

    private static string ExtractText(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return string.Join(' ', document.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
    }

    private async Task<ShiftStatus> ReadShiftStatusAsync(int shiftId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Shifts
            .Where(shift => shift.Id == shiftId)
            .Select(shift => shift.Status)
            .SingleAsync();
    }

    private async Task<ReportDeliveryStatus> ReadDeliveryStatusAsync(int shiftId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ShiftReportDeliveries
            .Where(delivery => delivery.ShiftId == shiftId)
            .Select(delivery => delivery.Status)
            .SingleAsync();
    }

    private static async Task<string> ReadAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The page did not include an antiforgery token.");
        return match.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> CloseAsync(HttpClient client, int shiftId, string token) =>
        client.PostAsync($"/Shifts/Close/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));
}
