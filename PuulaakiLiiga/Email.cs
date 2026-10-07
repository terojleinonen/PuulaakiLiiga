using System.Net;
using System.Net.Mail;

/// <summary>Sends mail through the SMTP server in the "Smtp" config section. Password reset is only offered when this and App:PublicUrl are set.</summary>
class EmailSender(IConfiguration config, ILogger<EmailSender> log)
{
    public bool Configured => !string.IsNullOrWhiteSpace(config["Smtp:Host"]) && !string.IsNullOrWhiteSpace(config["App:PublicUrl"]);
    public string PublicUrl => config["App:PublicUrl"]!.TrimEnd('/');

    public async Task SendAsync(string to, string subject, string body)
    {
        try
        {
            using var client = new SmtpClient(config["Smtp:Host"], config.GetValue("Smtp:Port", 587))
            {
                EnableSsl = config.GetValue("Smtp:EnableSsl", true),
                Credentials = string.IsNullOrEmpty(config["Smtp:User"]) ? null : new NetworkCredential(config["Smtp:User"], config["Smtp:Password"]),
            };
            using var msg = new MailMessage(config["Smtp:From"] ?? config["Smtp:User"] ?? "puulaakiliiga@localhost", to, subject, body);
            msg.BodyTransferEncoding = System.Net.Mime.TransferEncoding.SevenBit;   // keep the reset link on one unwrapped line
            await client.SendMailAsync(msg);
        }
        catch (Exception e) { log.LogError(e, "Could not send email"); }
    }
}
