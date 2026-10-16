using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;
using ShiftHandover.Web.Services;

namespace ShiftHandover.Tests;

public class ShiftManagementTests : IClassFixture<ShiftManagementTests.Factory>
{
    private const string Password = "Test-Password-123!";
    private const string FirstEmail = "supervisor-one@example.test";
    private const string SecondEmail = "supervisor-two@example.test";
    private const string RegularEmail = "regular-user@example.test";
    private const string FirstSupervisorId = "stage3-supervisor-one";
    private const string SecondSupervisorId = "stage3-supervisor-two";
    private readonly Factory _factory;

    public ShiftManagementTests(Factory factory) => _factory = factory;

    public class Factory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database=ShiftHandoverShifts_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(_connectionString));
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
            await CreateUserAsync(users, FirstEmail, FirstSupervisorId, "Supervisor One", isSupervisor: true);
            await CreateUserAsync(users, SecondEmail, SecondSupervisorId, "Supervisor Two", isSupervisor: true);
            await CreateUserAsync(users, RegularEmail, "stage3-regular-user", "Regular User", isSupervisor: false);
        }

        public new async Task DisposeAsync()
        {
            using (var scope = Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureDeletedAsync();
            }

            await base.DisposeAsync();
        }

        private static async Task CreateUserAsync(
            UserManager<ApplicationUser> users,
            string email,
            string id,
            string fullName,
            bool isSupervisor)
        {
            var result = await users.CreateAsync(new ApplicationUser
            {
                Id = id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName
            }, Password);
            Assert.True(result.Succeeded);

            if (isSupervisor)
            {
                var createdUser = await users.FindByIdAsync(id)
                    ?? throw new InvalidOperationException("The newly created test user could not be loaded.");
                var roleResult = await users.AddToRoleAsync(createdUser, DevelopmentSupervisorSeeder.SupervisorRole);
                Assert.True(roleResult.Succeeded);
            }
        }
    }

    [Fact]
    public async Task UnauthenticatedUser_IsRedirectedToLogin()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/Shifts");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AuthenticatedNonSupervisor_IsForbidden()
    {
        using var client = await CreateAuthenticatedClientAsync(RegularEmail);

        var response = await client.GetAsync("/Shifts");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ClaimAvailableShift_UsesSignedInSupervisorNotSubmittedId()
    {
        var shiftId = await CreateShiftAsync("Claim test shift");
        using var client = await CreateAuthenticatedClientAsync(FirstEmail);
        var token = await ReadAntiforgeryTokenAsync(client, $"/Shifts/Details/{shiftId}");

        var response = await ClaimAsync(client, shiftId, token, "some-other-user-id");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith($"/Shifts/Details/{shiftId}", response.Headers.Location!.OriginalString);
        var shift = await ReadShiftAsync(shiftId);
        Assert.Equal(ShiftStatus.Claimed, shift.Status);
        Assert.Equal(FirstSupervisorId, shift.ClaimedBySupervisorId);
        Assert.NotNull(shift.ClaimedAt);
    }

    [Fact]
    public async Task CompetingSupervisors_OnlyOneCanClaimShift()
    {
        var shiftId = await CreateShiftAsync("Competing claim shift");
        using var firstClient = await CreateAuthenticatedClientAsync(FirstEmail);
        using var secondClient = await CreateAuthenticatedClientAsync(SecondEmail);
        var firstToken = await ReadAntiforgeryTokenAsync(firstClient, $"/Shifts/Details/{shiftId}");
        var secondToken = await ReadAntiforgeryTokenAsync(secondClient, $"/Shifts/Details/{shiftId}");

        var responses = await Task.WhenAll(
            ClaimAsync(firstClient, shiftId, firstToken),
            ClaimAsync(secondClient, shiftId, secondToken));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Redirect, response.StatusCode));
        Assert.Single(responses, response => response.Headers.Location!.OriginalString.EndsWith($"/Shifts/Details/{shiftId}"));
        Assert.Single(responses, response => response.Headers.Location!.OriginalString.EndsWith("/Shifts"));
        var shift = await ReadShiftAsync(shiftId);
        Assert.Contains(shift.ClaimedBySupervisorId, new[] { FirstSupervisorId, SecondSupervisorId });
        Assert.Equal(ShiftStatus.Claimed, shift.Status);
        Assert.NotNull(shift.ClaimedAt);
    }

    [Fact]
    public async Task DailyShiftGeneration_IsConcurrentAndIdempotent_AndPreservesClosedShifts()
    {
        var bahrainDate = new DateOnly(2035, 10, 16);

        await Task.WhenAll(
            EnsureDailyShiftsAsync(bahrainDate),
            EnsureDailyShiftsAsync(bahrainDate));

        List<Shift> generated;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            generated = await db.Shifts.AsNoTracking()
                .Where(shift =>
                    (shift.ShiftDate == bahrainDate || shift.ShiftDate == bahrainDate.AddDays(1)) &&
                    (shift.Title == "Morning Operations" || shift.Title == "Evening Operations" || shift.Title == "Overnight Operations"))
                .OrderBy(shift => shift.ShiftDate)
                .ThenBy(shift => shift.StartTime)
                .ToListAsync();
        }

        Assert.Equal(3, generated.Count);
        Assert.Collection(generated,
            shift =>
            {
                Assert.Equal("Morning Operations", shift.Title);
                Assert.Equal(bahrainDate, shift.ShiftDate);
                Assert.Equal(new TimeOnly(6, 0), shift.StartTime);
                Assert.Equal(new TimeOnly(14, 0), shift.EndTime);
            },
            shift =>
            {
                Assert.Equal("Evening Operations", shift.Title);
                Assert.Equal(bahrainDate, shift.ShiftDate);
                Assert.Equal(new TimeOnly(14, 0), shift.StartTime);
                Assert.Equal(new TimeOnly(22, 0), shift.EndTime);
            },
            shift =>
            {
                Assert.Equal("Overnight Operations", shift.Title);
                Assert.Equal(bahrainDate.AddDays(1), shift.ShiftDate);
                Assert.Equal(new TimeOnly(22, 0), shift.StartTime);
                Assert.Equal(new TimeOnly(6, 0), shift.EndTime);
            });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var overnight = await db.Shifts.SingleAsync(shift =>
                shift.Title == "Overnight Operations" && shift.ShiftDate == bahrainDate.AddDays(1));
            overnight.Claim(FirstSupervisorId, DateTimeOffset.UtcNow);
            overnight.Close(FirstSupervisorId, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await EnsureDailyShiftsAsync(bahrainDate);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(3, await db.Shifts.CountAsync(shift =>
                (shift.ShiftDate == bahrainDate || shift.ShiftDate == bahrainDate.AddDays(1)) &&
                (shift.Title == "Morning Operations" || shift.Title == "Evening Operations" || shift.Title == "Overnight Operations")));
            Assert.Equal(ShiftStatus.Closed,
                (await db.Shifts.SingleAsync(shift =>
                    shift.Title == "Overnight Operations" && shift.ShiftDate == bahrainDate.AddDays(1))).Status);
        }
    }

    [Fact]
    public async Task Dashboard_ShowsGeneratedShiftsAndRefreshesCountsAfterClaimAndClose()
    {
        var bahrainDate = new DateOnly(2036, 10, 16);
        await EnsureDailyShiftsAsync(bahrainDate);
        using var client = await CreateAuthenticatedClientAsync(FirstEmail);

        var dashboard = await client.GetStringAsync("/");
        Assert.Contains("Morning Operations", dashboard);
        Assert.Contains("Evening Operations", dashboard);
        Assert.Contains("Overnight Operations", dashboard);
        await AssertDashboardCountsAsync(client);

        var shiftId = await GetShiftIdByTitleAsync("Morning Operations", bahrainDate);
        var token = await ReadAntiforgeryTokenAsync(client, $"/Shifts/Details/{shiftId}");
        Assert.Equal(HttpStatusCode.Redirect, (await ClaimAsync(client, shiftId, token)).StatusCode);
        await AssertDashboardCountsAsync(client);

        token = await ReadAntiforgeryTokenAsync(client, $"/Shifts/Details/{shiftId}");
        var closeResponse = await client.PostAsync($"/Shifts/Close/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, closeResponse.StatusCode);
        await AssertDashboardCountsAsync(client);
    }

    [Fact]
    public async Task Supervisor_CanClaimMultipleShifts()
    {
        var firstShiftId = await CreateShiftAsync("First multi-claim shift");
        var secondShiftId = await CreateShiftAsync("Second multi-claim shift");
        using var client = await CreateAuthenticatedClientAsync(FirstEmail);

        await ClaimShiftAsync(client, firstShiftId);
        await ClaimShiftAsync(client, secondShiftId);

        Assert.Equal(FirstSupervisorId, (await ReadShiftAsync(firstShiftId)).ClaimedBySupervisorId);
        Assert.Equal(FirstSupervisorId, (await ReadShiftAsync(secondShiftId)).ClaimedBySupervisorId);
    }

    [Fact]
    public async Task OtherSupervisor_CanViewClaimedShiftAndRecordsButCannotWriteOrReclaim()
    {
        var shiftId = await CreateShiftAsync("Ownership test shift");
        using var owner = await CreateAuthenticatedClientAsync(FirstEmail);
        var ownerToken = await ReadAntiforgeryTokenAsync(owner, $"/Shifts/Details/{shiftId}");
        await ClaimAsync(owner, shiftId, ownerToken);
        await SeedShiftRecordsAsync(shiftId);

        using var other = await CreateAuthenticatedClientAsync(SecondEmail);
        var list = await other.GetStringAsync("/Shifts");
        Assert.Contains("Ownership test shift", list);
        var shiftRow = FindShiftRow(list, "Ownership test shift");
        Assert.NotEmpty(shiftRow);
        Assert.Contains("Claimed", shiftRow);
        Assert.Contains(">Details<", shiftRow);
        Assert.DoesNotContain(">Claim<", shiftRow);

        var details = await other.GetStringAsync($"/Shifts/Details/{shiftId}");
        Assert.Contains("Supervisor One", details);
        Assert.Contains("Accidents", details);
        Assert.Contains("Incidents", details);
        Assert.Contains("Manpower", details);
        Assert.DoesNotContain("Close shift", details);

        var accident = await other.GetStringAsync($"/ShiftRecords/Accidents/{shiftId}");
        var incident = await other.GetStringAsync($"/ShiftRecords/Incidents/{shiftId}");
        var manpower = await other.GetStringAsync($"/ShiftRecords/Manpower/{shiftId}");
        Assert.Contains("Shared accident record", accident);
        Assert.Contains("Shared incident record", incident);
        Assert.Contains("Shared manpower note", manpower);
        Assert.DoesNotContain("Save accident", accident);
        Assert.DoesNotContain("Save incident", incident);
        Assert.DoesNotContain("Save manpower", manpower);
        await AssertRecordWritesDeniedAsync(other, shiftId);

        var otherToken = await ReadAntiforgeryTokenAsync(other, $"/Shifts/Details/{await CreateShiftAsync("Claim token source")}");
        var attempt = await ClaimAsync(other, shiftId, otherToken);
        Assert.Equal(HttpStatusCode.Redirect, attempt.StatusCode);
        Assert.Equal(FirstSupervisorId, (await ReadShiftAsync(shiftId)).ClaimedBySupervisorId);
    }

    [Fact]
    public async Task ClosedShift_IsVisibleAndReadOnlyForEverySupervisor()
    {
        var shiftId = await CreateClosedShiftAsync("Closed status test shift");
        await SeedShiftRecordsAsync(shiftId);
        using var owner = await CreateAuthenticatedClientAsync(FirstEmail);
        using var other = await CreateAuthenticatedClientAsync(SecondEmail);

        var list = await other.GetStringAsync("/Shifts");
        var shiftRow = FindShiftRow(list, "Closed status test shift");

        Assert.NotEmpty(shiftRow);
        Assert.Contains("Closed", shiftRow);
        Assert.Contains(">Details<", shiftRow);
        Assert.DoesNotContain(">Claim<", shiftRow);

        foreach (var client in new[] { owner, other })
        {
            var details = await client.GetStringAsync($"/Shifts/Details/{shiftId}");
            Assert.Contains("Closed", details);
            Assert.DoesNotContain("Close shift", details);

            var accident = await client.GetStringAsync($"/ShiftRecords/Accidents/{shiftId}");
            var incident = await client.GetStringAsync($"/ShiftRecords/Incidents/{shiftId}");
            var manpower = await client.GetStringAsync($"/ShiftRecords/Manpower/{shiftId}");
            Assert.Contains("Shared accident record", accident);
            Assert.Contains("Shared incident record", incident);
            Assert.Contains("Shared manpower note", manpower);
            Assert.DoesNotContain("Save accident", accident);
            Assert.DoesNotContain("Save incident", incident);
            Assert.DoesNotContain("Save manpower", manpower);
            await AssertRecordWritesDeniedAsync(client, shiftId);
        }
    }

    [Fact]
    public async Task OnlyOwnerCanCloseOpenShift()
    {
        var shiftId = await CreateShiftAsync("Close permission shift");
        using var owner = await CreateAuthenticatedClientAsync(FirstEmail);
        await ClaimShiftAsync(owner, shiftId);
        using var other = await CreateAuthenticatedClientAsync(SecondEmail);

        var ownerDetails = await owner.GetStringAsync($"/Shifts/Details/{shiftId}");
        Assert.Contains("Close shift", ownerDetails);
        var ownerToken = await ReadAntiforgeryTokenAsync(owner, $"/Shifts/Details/{shiftId}");
        var otherTokenSource = await CreateShiftAsync("Close token source");
        var otherToken = await ReadAntiforgeryTokenAsync(other, $"/Shifts/Details/{otherTokenSource}");

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/Shifts/Close/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = otherToken
        }))).StatusCode);
        Assert.Equal(ShiftStatus.Claimed, (await ReadShiftAsync(shiftId)).Status);

        var closeResponse = await owner.PostAsync($"/Shifts/Close/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ownerToken
        }));
        Assert.Equal(HttpStatusCode.Redirect, closeResponse.StatusCode);
        Assert.Equal(ShiftStatus.Closed, (await ReadShiftAsync(shiftId)).Status);
    }

    [Fact]
    public async Task Supervisor_CanCreateAccidentIncidentAndManpowerRecordsForClaimedShift()
    {
        var shiftId = await CreateShiftAsync("Record creation shift");
        using var client = await CreateAuthenticatedClientAsync(FirstEmail);
        await ClaimShiftAsync(client, shiftId);

        var accidentToken = await ReadAntiforgeryTokenAsync(client, $"/ShiftRecords/Accidents/{shiftId}");
        var accidentResponse = await client.PostAsync($"/ShiftRecords/Accidents/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = accidentToken,
            ["Input.OccurredAt"] = "2026-10-16T09:30:00+00:00",
            ["Input.Location"] = "Loading bay",
            ["Input.Description"] = "Minor hand injury",
            ["Input.Severity"] = "Low",
            ["Input.InjuredPerson"] = "Worker A",
            ["Input.ActionTaken"] = "First aid provided"
        }));

        var incidentToken = await ReadAntiforgeryTokenAsync(client, $"/ShiftRecords/Incidents/{shiftId}");
        var incidentResponse = await client.PostAsync($"/ShiftRecords/Incidents/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = incidentToken,
            ["Input.OccurredAt"] = "2026-10-16T10:15:00+00:00",
            ["Input.Category"] = "Equipment",
            ["Input.Description"] = "Conveyor stopped unexpectedly",
            ["Input.Resolved"] = "true",
            ["Input.FollowUpRequired"] = "true"
        }));

        var manpowerToken = await ReadAntiforgeryTokenAsync(client, $"/ShiftRecords/Manpower/{shiftId}");
        var manpowerResponse = await client.PostAsync($"/ShiftRecords/Manpower/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = manpowerToken,
            ["Input.Department"] = "Operations",
            ["Input.Role"] = "Technician",
            ["Input.Required"] = "5",
            ["Input.Present"] = "4",
            ["Input.Absent"] = "1",
            ["Input.Notes"] = "One scheduled absence"
        }));

        Assert.Equal(HttpStatusCode.Redirect, accidentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, incidentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, manpowerResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var accident = await db.Accidents.AsNoTracking().SingleAsync(record => record.ShiftId == shiftId);
        var incident = await db.Incidents.AsNoTracking().SingleAsync(record => record.ShiftId == shiftId);
        var manpower = await db.ManpowerRecords.AsNoTracking().SingleAsync(record => record.ShiftId == shiftId);

        Assert.Equal("Loading bay", accident.Location);
        Assert.Equal(FirstSupervisorId, accident.RecordedBySupervisorId);
        Assert.Equal("Equipment", incident.Category);
        Assert.Equal(FirstSupervisorId, incident.RecordedBySupervisorId);
        Assert.Equal(5, manpower.Required);
        Assert.Equal(4, manpower.Present);
        Assert.Equal(1, manpower.Absent);
        Assert.Equal(FirstSupervisorId, manpower.RecordedBySupervisorId);
    }

    [Fact]
    public async Task InvalidAccidentIncidentAndManpowerInputs_AreRejected()
    {
        var shiftId = await CreateShiftAsync("Record validation shift");
        using var client = await CreateAuthenticatedClientAsync(FirstEmail);
        await ClaimShiftAsync(client, shiftId);

        var accidentToken = await ReadAntiforgeryTokenAsync(client, $"/ShiftRecords/Accidents/{shiftId}");
        var accidentResponse = await client.PostAsync($"/ShiftRecords/Accidents/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = accidentToken,
            ["Input.Location"] = "",
            ["Input.Description"] = ""
        }));

        var incidentToken = await ReadAntiforgeryTokenAsync(client, $"/ShiftRecords/Incidents/{shiftId}");
        var incidentResponse = await client.PostAsync($"/ShiftRecords/Incidents/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = incidentToken,
            ["Input.Category"] = "",
            ["Input.Description"] = ""
        }));

        var manpowerToken = await ReadAntiforgeryTokenAsync(client, $"/ShiftRecords/Manpower/{shiftId}");
        var manpowerResponse = await client.PostAsync($"/ShiftRecords/Manpower/{shiftId}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = manpowerToken,
            ["Input.Department"] = "",
            ["Input.Role"] = "Technician",
            ["Input.Required"] = "-1",
            ["Input.Present"] = "0",
            ["Input.Absent"] = "0"
        }));

        Assert.Equal(HttpStatusCode.OK, accidentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, incidentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, manpowerResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.Accidents.Where(record => record.ShiftId == shiftId).ToListAsync());
        Assert.Empty(await db.Incidents.Where(record => record.ShiftId == shiftId).ToListAsync());
        Assert.Empty(await db.ManpowerRecords.Where(record => record.ShiftId == shiftId).ToListAsync());
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

    private async Task<int> CreateShiftAsync(string title)
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
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift.Id;
    }

    private async Task EnsureDailyShiftsAsync(DateOnly bahrainDate)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await DailyShiftGenerator.EnsureShiftsForDateAsync(db, bahrainDate);
    }

    private async Task<int> GetShiftIdByTitleAsync(string title, DateOnly shiftDate)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Shifts.Where(shift => shift.Title == title && shift.ShiftDate == shiftDate)
            .Select(shift => shift.Id)
            .SingleAsync();
    }

    private async Task AssertDashboardCountsAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal(await db.Shifts.CountAsync(), ReadDashboardMetric(html, "metric-total"));
        Assert.Equal(await db.Shifts.CountAsync(shift => shift.Status == ShiftStatus.Available), ReadDashboardMetric(html, "metric-available"));
        Assert.Equal(await db.Shifts.CountAsync(shift => shift.Status == ShiftStatus.Claimed), ReadDashboardMetric(html, "metric-progress"));
        Assert.Equal(await db.Shifts.CountAsync(shift => shift.Status == ShiftStatus.Closed), ReadDashboardMetric(html, "metric-closed"));
    }

    private static int ReadDashboardMetric(string html, string metricClass)
    {
        var match = Regex.Match(html, $"<article class=\\\"metric-card {metricClass}\\\">[\\s\\S]*?<p class=\\\"metric-value\\\">(\\d+)</p>");
        Assert.True(match.Success, $"The dashboard did not render the {metricClass} metric.");
        return int.Parse(match.Groups[1].Value);
    }

    private async Task<int> CreateClosedShiftAsync(string title)
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
        shift.Claim(FirstSupervisorId, DateTimeOffset.UtcNow);
        shift.Close(FirstSupervisorId, DateTimeOffset.UtcNow);
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift.Id;
    }

    private async Task SeedShiftRecordsAsync(int shiftId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Accidents.Add(new Accident
        {
            ShiftId = shiftId,
            OccurredAt = new DateTimeOffset(2026, 10, 16, 9, 30, 0, TimeSpan.Zero),
            Location = "Shared accident record",
            Description = "Seeded for visibility test",
            Severity = AccidentSeverity.Low,
            RecordedBySupervisorId = FirstSupervisorId
        });
        db.Incidents.Add(new Incident
        {
            ShiftId = shiftId,
            OccurredAt = new DateTimeOffset(2026, 10, 16, 10, 15, 0, TimeSpan.Zero),
            Category = "Shared incident record",
            Description = "Seeded for visibility test",
            RecordedBySupervisorId = FirstSupervisorId
        });
        db.ManpowerRecords.Add(new ManpowerRecord
        {
            ShiftId = shiftId,
            Department = "Operations",
            Role = "Technician",
            Required = 5,
            Present = 4,
            Absent = 1,
            Notes = "Shared manpower note",
            RecordedBySupervisorId = FirstSupervisorId
        });
        await db.SaveChangesAsync();
    }

    private async Task AssertRecordWritesDeniedAsync(HttpClient client, int shiftId)
    {
        var tokenSourceId = await CreateShiftAsync("Record token source");
        var token = await ReadAntiforgeryTokenAsync(client, $"/Shifts/Details/{tokenSourceId}");
        foreach (var action in new[] { "Accidents", "Incidents", "Manpower" })
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            });
            var response = await client.PostAsync($"/ShiftRecords/{action}/{shiftId}", form);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static string FindShiftRow(string html, string title) =>
        Regex.Matches(html, @"<tr>[\s\S]*?</tr>")
            .Cast<Match>()
            .Select(match => match.Value)
            .SingleOrDefault(row => row.Contains(title)) ?? string.Empty;

    private async Task ClaimShiftAsync(HttpClient client, int shiftId)
    {
        var token = await ReadAntiforgeryTokenAsync(client, $"/Shifts/Details/{shiftId}");
        var response = await ClaimAsync(client, shiftId, token);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private async Task<Shift> ReadShiftAsync(int id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Shifts
            .AsNoTracking()
            .SingleAsync(shift => shift.Id == id);
    }

    private static async Task<string> ReadAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The page did not include an antiforgery token.");
        return match.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> ClaimAsync(
        HttpClient client,
        int shiftId,
        string token,
        string? submittedSupervisorId = null)
    {
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };
        if (submittedSupervisorId is not null)
        {
            form["ClaimedBySupervisorId"] = submittedSupervisorId;
        }

        return client.PostAsync($"/Shifts/Claim/{shiftId}", new FormUrlEncodedContent(form));
    }
}