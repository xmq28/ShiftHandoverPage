using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ShiftHandover.Web.Services;

public sealed class SmtpShiftReportEmailSender(IOptions<SmtpOptions> options) : IShiftReportEmailSender
{
    public async Task SendAsync(IReadOnlyCollection<string> recipients, string fileName, byte[] pdf)
    {
        var settings = options.Value;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Host)) missing.Add("Email:Smtp:Host");
        if (settings.Port is < 1 or > 65535) missing.Add("Email:Smtp:Port");
        if (string.IsNullOrWhiteSpace(settings.UserName)) missing.Add("Email:Smtp:UserName");
        if (string.IsNullOrWhiteSpace(settings.Password)) missing.Add("Email:Smtp:Password");
        if (string.IsNullOrWhiteSpace(settings.FromAddress)) missing.Add("Email:Smtp:FromAddress");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Email delivery is not configured. Set: {string.Join(", ", missing)}.");
        }

        if (!Enum.TryParse<SecureSocketOptions>(settings.Security, true, out var security)
            || security is SecureSocketOptions.None or SecureSocketOptions.Auto or SecureSocketOptions.StartTlsWhenAvailable)
        {
            throw new InvalidOperationException("Email:Smtp:Security must be StartTls or SslOnConnect.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        foreach (var recipient in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = "Closed shift handover report";
        var body = new BodyBuilder
        {
            TextBody = "The attached PDF contains the shift details and recorded handover information."
        };
        body.Attachments.Add(fileName, pdf, ContentType.Parse("application/pdf"));
        message.Body = body.ToMessageBody();

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, security);
        await client.AuthenticateAsync(settings.UserName, settings.Password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
