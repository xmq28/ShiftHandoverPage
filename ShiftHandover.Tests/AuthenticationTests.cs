using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftHandover.Web.Data;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Tests;

public class AuthenticationTests : IClassFixture<AuthenticationTests.Factory>
{
    private const string Email = "tester@example.test";
    private const string Password = "Test-Password-123!";
    private readonly Factory _factory;

    public AuthenticationTests(Factory factory) => _factory = factory;

    public class Factory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _connectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database=ShiftHandoverAuth_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(_connectionString));
            });
        }

        public async Task InitializeAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var result = await users.CreateAsync(
                new ApplicationUser { UserName = Email, Email = Email, FullName = "Tester" }, Password);
            Assert.True(result.Succeeded);
        }

        public new async Task DisposeAsync()
        {
            using (var scope = Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureDeletedAsync();
            }
            await base.DisposeAsync();
        }
    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost")
    });

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password)
    {
        var page = await client.GetStringAsync("/Account/Login");
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = page.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var token = page[start..page.IndexOf('"', start)];

        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
    }

    [Fact]
    public async Task UnauthenticatedRequest_RedirectsToLogin()
    {
        var response = await CreateClient().GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location!.OriginalString.Replace("https://localhost", ""));
    }

    [Fact]
    public async Task ValidCredentials_SignInAndSetCookie()
    {
        var client = CreateClient();
        var response = await PostLoginAsync(client, Email, Password);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ShiftHandover.Auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);

        var home = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.OK, home.StatusCode);
    }

    [Theory]
    [InlineData(Email, "Wrong-Password-1!")]
    [InlineData("nobody@example.test", Password)]
    public async Task InvalidCredentials_ShowErrorAndDoNotSignIn(string email, string password)
    {
        var client = CreateClient();
        var response = await PostLoginAsync(client, email, password);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email or password.", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(response.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.StartsWith("ShiftHandover.Auth=")));
    }

    [Fact]
    public async Task Logout_ClearsSession()
    {
        var client = CreateClient();
        await PostLoginAsync(client, Email, Password);

        var home = await client.GetStringAsync("/");
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = home.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var token = home[start..home.IndexOf('"', start)];

        var logout = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, logout.StatusCode);

        var after = await client.GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, after.StatusCode);
    }
}
