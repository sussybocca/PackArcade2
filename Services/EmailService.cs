using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Threading.Tasks;

namespace PackArcade2.Services
{
    public interface IEmailService
    {
        Task<bool> SendEmailAsync(string to, string subject, string body);
    }

    public class EmailService : IEmailService
    {
        private readonly EmailSettings _settings;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IOptions<EmailSettings> settings, ILogger<EmailService> logger)
        {
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<bool> SendEmailAsync(string to, string subject, string body)
        {
            try
            {
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("PackArcade2 Security", _settings.From));
                message.To.Add(new MailboxAddress("Administrator", to));
                message.Subject = subject;
                
                var bodyBuilder = new BodyBuilder
                {
                    TextBody = body
                };
                message.Body = bodyBuilder.ToMessageBody();

                using var client = new SmtpClient();
                
                await ConnectWithSecurityAsync(client);
                
                // Authenticate if credentials provided
                if (!string.IsNullOrEmpty(_settings.Username))
                {
                    await client.AuthenticateAsync(_settings.Username, _settings.Password);
                }
                
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
                
                _logger.LogInformation($"Email sent to {to}: {subject}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send email to {to}");
                return false;
            }
        }

        private async Task ConnectWithSecurityAsync(SmtpClient client)
        {
            if (_settings.Port == 587)
            {
                // Port 587 uses STARTTLS
                await client.ConnectAsync(_settings.SmtpServer, _settings.Port, SecureSocketOptions.StartTls);
            }
            else if (_settings.Port == 465)
            {
                // Port 465 uses direct SSL
                await client.ConnectAsync(_settings.SmtpServer, _settings.Port, SecureSocketOptions.SslOnConnect);
            }
            else
            {
                await client.ConnectAsync(_settings.SmtpServer, _settings.Port, SecureSocketOptions.Auto);
            }
        }
    }

    public class EmailSettings
    {
        public string SmtpServer { get; set; } = "smtp.gmail.com";
        public int Port { get; set; } = 587;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string From { get; set; } = "support@packarcade.win";
        public bool EnableSsl { get; set; } = true;
    }
}