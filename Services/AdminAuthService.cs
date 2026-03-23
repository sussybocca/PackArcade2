using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace PackArcade2.Services
{
    public class AdminAuthService
    {
        private string _currentPassword = "";
        private readonly IEmailService _emailService;
        private readonly ILogger<AdminAuthService> _logger;
        private readonly AdminSettings _settings;

        public AdminAuthService(
            IEmailService emailService, 
            ILogger<AdminAuthService> logger, 
            IOptions<AdminSettings> settings)
        {
            _emailService = emailService;
            _logger = logger;
            _settings = settings.Value;
            
            GenerateNewPassword();
        }

        public string GetCurrentPassword()
        {
            return _currentPassword;
        }

        public void GenerateNewPassword()
        {
            _currentPassword = GenerateSecurePassword(24);
            
            // Save to app data
            try
            {
                var appDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                Directory.CreateDirectory(appDataPath);
                var passwordFile = Path.Combine(appDataPath, "current_password.txt");
                File.WriteAllText(passwordFile, _currentPassword);
                _logger.LogInformation($"Password saved to: {passwordFile}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save password");
            }
            
            // Save to desktop
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var desktopPasswordFile = Path.Combine(desktopPath, "PACKARCADE_ADMIN_PASSWORD.txt");
                var content = $@"
╔═══════════════════════════════════════════════════════════════════╗
║                 PACKARCADE2 ADMIN PASSWORD                        ║
╚═══════════════════════════════════════════════════════════════════╝

PASSWORD: {_currentPassword}

Login URL: http://localhost:5000/admin/login

This password was generated on: {DateTime.Now}

Keep this file secure and delete it after use.

═══════════════════════════════════════════════════════════════════
";
                File.WriteAllText(desktopPasswordFile, content);
                _logger.LogInformation($"Password also saved to desktop: {desktopPasswordFile}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save password to desktop");
            }
            
            // Print to console
            Console.WriteLine("");
            Console.WriteLine("╔═══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                    ADMIN PASSWORD GENERATED                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════╝");
            Console.WriteLine($"");
            Console.WriteLine($"  PASSWORD: {_currentPassword}");
            Console.WriteLine($"");
            Console.WriteLine($"  Login at: http://localhost:5000/admin/login");
            Console.WriteLine($"");
            Console.WriteLine("╔═══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("");
            
            // Try to send email
            _ = Task.Run(async () =>
            {
                try
                {
                    var emailBody = $@"
PACKARCADE2 ADMIN PASSWORD

Password: {_currentPassword}

Login at: http://localhost:5000/admin/login

This password was generated on: {DateTime.Now}

";
                    var adminEmail = _settings.AdminEmail ?? "babyyodacutefry@gmail.com";
                    await _emailService.SendEmailAsync(adminEmail, "[PACKARCADE2] Admin Password", emailBody);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background email send failed");
                }
            });
        }

        public bool ValidatePassword(string password)
        {
            return password == _currentPassword;
        }

        public void RegeneratePassword()
        {
            GenerateNewPassword();
        }

        private static string GenerateSecurePassword(int length)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*";
            var result = new StringBuilder(length);
            using var rng = RandomNumberGenerator.Create();
            var buffer = new byte[sizeof(uint)];
            
            for (int i = 0; i < length; i++)
            {
                rng.GetBytes(buffer);
                uint num = BitConverter.ToUInt32(buffer, 0);
                result.Append(chars[(int)(num % (uint)chars.Length)]);
            }
            
            return result.ToString();
        }
    }
    
    public class AdminSettings
    {
        public string ServerDomain { get; set; } = "packarcade.win";
        public string AdminEmail { get; set; } = "babyyodacutefry@gmail.com";
    }
}