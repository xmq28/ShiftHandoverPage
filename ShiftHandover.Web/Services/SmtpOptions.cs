namespace ShiftHandover.Web.Services;

public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Shift Handover";
    public string Security { get; set; } = "StartTls";
}

public interface IShiftReportEmailSender
{
    Task SendAsync(IReadOnlyCollection<string> recipients, string fileName, byte[] pdf);
}
