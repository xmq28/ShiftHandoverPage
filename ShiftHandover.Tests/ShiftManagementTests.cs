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
    public async Task OtherSupervisor_CannotViewOrReclaimClaimedShift()
    {
        var shiftId = await CreateShiftAsync("Ownership test shift");
        using var owner = await CreateAuthenticatedClientAsync(FirstEmail);
        var ownerToken = await ReadAntiforgeryTokenAsync(owner, $"/Shifts/Details/{shiftId}");
        await ClaimAsync(owner, shiftId, ownerToken);

        using var other = await CreateAuthenticatedClientAsync(SecondEmail);
        var list = await other.GetStringAsync("/Shifts");
        Assert.Contains("Ownership test shift", list);
        var shiftRow = Regex.Match(list, @"<tr>[\s\S]*?Ownership test shift[\s\S]*?</tr>").Value;
        Assert.NotEmpty(shiftRow);
        Assert.Contains("Claimed", shiftRow);
        Assert.DoesNotContain(">Claim<", shiftRow);

        var details = await other.GetAsync($"/Shifts/Details/{shiftId}");
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);

        var otherToken = await ReadAntiforgeryTokenAsync(other, "/Shifts");
        var attempt = await ClaimAsync(other, shiftId, otherToken);
        Assert.Equal(HttpStatusCode.Redirect, attempt.StatusCode);
        Assert.Equal(FirstSupervisorId, (await ReadShiftAsync(shiftId)).ClaimedBySupervisorId);
    }

    [Fact]
    public async Task ClosedShift_IsVisibleWithClosedStatusAndNoClaimButton()
    {
        var shiftId = await CreateClosedShiftAsync("Closed status test shift");
        using var other = await CreateAuthenticatedClientAsync(SecondEmail);

        var list = await other.GetStringAsync("/Shifts");
        var shiftRow = Regex.Match(list, @"<tr>[\s\S]*?Closed status test shift[\s\S]*?</tr>").Value;

        Assert.NotEmpty(shiftRow);
        Assert.Contains("Closed", shiftRow);
        Assert.DoesNotContain(">Claim<", shiftRow);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/Shifts/Details/{shiftId}")).StatusCode);
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

    [Fact]
    public async Task OtherSupervisor_CannotReadOrCreateRecordsForClaimedShift()
    {
        var shiftId = await CreateShiftAsync("Protected records shift");
        using var owner = await CreateAuthenticatedClientAsync(FirstEmail);
        await ClaimShiftAsync(owner, shiftId);

        using var other = await CreateAuthenticatedClientAsync(SecondEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/ShiftRecords/Accidents/{shiftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/ShiftRecords/Incidents/{shiftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/ShiftRecords/Manpower/{shiftId}")).StatusCode);

        var token = await ReadAntiforgeryTokenAsync(other, "/Shifts");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.OccurredAt"] = "2026-10-16T11:00:00+00:00",
            ["Input.Location"] = "Restricted area",
            ["Input.Description"] = "Unauthorized attempt",
            ["Input.Severity"] = "Low"
        });

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/ShiftRecords/Accidents/{shiftId}", form)).StatusCode);

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