using Microsoft.AspNetCore.Identity;
using ShiftHandover.Web.Models;

namespace ShiftHandover.Web.Data;

// Development only. Credentials come from user-secrets or environment variables, never source code.
public static class DevelopmentSupervisorSeeder
{
    public const string SupervisorRole = "Supervisor";

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = services.GetRequiredService<ILogger<ApplicationUser>>();

        if (!await roleManager.RoleExistsAsync(SupervisorRole))
        {
            await roleManager.CreateAsync(new IdentityRole(SupervisorRole));
        }

        foreach (var key in new[] { "Supervisor1", "Supervisor2" })
        {
            var section = configuration.GetSection($"SeedSupervisors:{key}");
            var email = section["Email"];
            var password = section["Password"];
            var userId = section["UserId"];
            var fullName = section["FullName"] ?? key;

            if (string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning("Email setting for {Key} is missing; skipping.", key);
                continue;
            }

            var user = string.IsNullOrWhiteSpace(userId)
                ? await userManager.FindByEmailAsync(email)
                : await userManager.FindByIdAsync(userId);
            if (!string.IsNullOrWhiteSpace(userId) && user is null)
            {
                logger.LogError("Configured user ID for {Key} was not found; skipping email update.", key);
                continue;
            }

            if (user is null)
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    logger.LogWarning("Password setting for new {Key} is missing; skipping.", key);
                    continue;
                }

                user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = fullName };
                var result = await userManager.CreateAsync(user, password);
                if (!result.Succeeded)
                {
                    logger.LogError("Could not seed {Key}: {Errors}", key, string.Join("; ", result.Errors.Select(e => e.Code)));
                    continue;
                }
            }
            else if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                var updateResult = await userManager.SetEmailAsync(user, email);
                if (!updateResult.Succeeded)
                {
                    logger.LogError("Could not update {Key} email: {Errors}", key, string.Join("; ", updateResult.Errors.Select(error => error.Code)));
                    continue;
                }
            }

            if (!await userManager.IsInRoleAsync(user, SupervisorRole))
            {
                await userManager.AddToRoleAsync(user, SupervisorRole);
            }
        }
    }
}
