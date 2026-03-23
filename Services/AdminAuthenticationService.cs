using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PackArcade2.Services
{
    public class AdminAuthenticationService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly AdminAuthService _adminAuth;
        private readonly IPWhitelistService _ipWhitelist;
        private readonly ILogger<AdminAuthenticationService> _logger;

        public AdminAuthenticationService(
            IHttpContextAccessor httpContextAccessor,
            AdminAuthService adminAuth,
            IPWhitelistService ipWhitelist,
            ILogger<AdminAuthenticationService> logger)
        {
            _httpContextAccessor = httpContextAccessor;
            _adminAuth = adminAuth;
            _ipWhitelist = ipWhitelist;
            _logger = logger;
        }

        public async Task<bool> SignIn(string password)
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null) return false;
            
            var clientIp = context.Connection.RemoteIpAddress?.ToString();
            
            // Handle proxy forwarding
            if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                clientIp = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            }
            
            // Log the password being attempted (for debugging)
            _logger.LogInformation($"Attempting login with password: {password}");
            
            // Get the stored password
            var storedPassword = _adminAuth.GetCurrentPassword();
            _logger.LogInformation($"Stored password: {storedPassword}");
            
            // Direct string comparison without any encoding/decoding
            var isValid = password == storedPassword;
            
            _logger.LogInformation($"Password match: {isValid}");
            
            if (!isValid)
            {
                _logger.LogWarning($"Failed admin login attempt from IP: {clientIp}");
                return false;
            }
            
            // Add IP to whitelist on successful login
            if (!string.IsNullOrEmpty(clientIp))
            {
                _ipWhitelist.AddIP(clientIp);
            }
            
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "administrator"),
                new Claim(ClaimTypes.Role, "administrator"),
                new Claim("LoginTime", DateTime.UtcNow.ToString("o"))
            };
            
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
            
            _logger.LogInformation($"Admin logged in successfully from IP: {clientIp}");
            return true;
        }

        public async Task SignOut()
        {
            var context = _httpContextAccessor.HttpContext;
            if (context != null)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                _logger.LogInformation("Admin logged out");
            }
        }
        
        public bool IsAuthenticated()
        {
            return _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
        }
    }
}