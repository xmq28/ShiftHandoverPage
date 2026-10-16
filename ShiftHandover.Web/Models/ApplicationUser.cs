using Microsoft.AspNetCore.Identity;

namespace ShiftHandover.Web.Models;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
}