using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PackArcade2.Services;
using System.Threading.Tasks;

namespace PackArcade2.Middleware
{
    public class FirewallMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<FirewallMiddleware> _logger;

        public FirewallMiddleware(RequestDelegate next, ILogger<FirewallMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IPWhitelistService whitelist)
        {
            var path = context.Request.Path.Value ?? "";
            
            // ALWAYS allow access to login page and login API - regardless of IP
            if (path == "/admin/login" || 
                path == "/api/admin/login" || 
                path.StartsWith("/admin/login?") ||
                path == "/admin/denied" ||
                path.StartsWith("/_blazor") ||
                path.StartsWith("/css") ||
                path.StartsWith("/js") ||
                path.StartsWith("/lib"))
            {
                await _next(context);
                return;
            }
            
            // Check if this is an admin route
            if (path.StartsWith("/admin") || path.StartsWith("/api/admin"))
            {
                var remoteIp = context.Connection.RemoteIpAddress?.ToString();
                
                // Handle X-Forwarded-For for reverse proxies
                if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
                {
                    remoteIp = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
                }
                
                // If no IPs are whitelisted yet, allow the first admin access
                var allowedIPs = whitelist.GetAllowedIPs();
                if (!allowedIPs.Any())
                {
                    _logger.LogWarning($"No IPs whitelisted yet. Allowing first admin access from: {remoteIp}");
                    // Don't block - let them through to login
                    await _next(context);
                    return;
                }
                
                // Check if IP is whitelisted
                if (!whitelist.IsAllowed(remoteIp))
                {
                    _logger.LogWarning($"Blocked admin access from unauthorized IP: {remoteIp}");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head>
    <title>Access Denied - PackArcade2</title>
    <style>
        body {
            background: linear-gradient(135deg, #0a0e1a 0%, #0f111a 100%);
            font-family: 'Segoe UI', Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
        }
        .denied-container {
            background: rgba(15, 17, 26, 0.95);
            border-radius: 16px;
            padding: 40px;
            width: 500px;
            text-align: center;
            border: 1px solid #ff4444;
        }
        h1 { color: #ff4444; margin-bottom: 20px; }
        p { color: #ccc; margin-bottom: 20px; }
        .ip { 
            background: #1a1e2a; 
            padding: 12px; 
            border-radius: 8px; 
            font-family: monospace;
            color: #00ffaa;
            margin: 20px 0;
        }
        button {
            background: #00ffaa;
            color: #0a0e1a;
            padding: 12px 24px;
            border: none;
            border-radius: 8px;
            cursor: pointer;
            font-weight: bold;
        }
        button:hover { background: #00cc88; }
    </style>
</head>
<body>
    <div class='denied-container'>
        <h1>🔒 Access Denied</h1>
        <p>Your IP address is not authorized to access the admin panel.</p>
        <div class='ip'>Your IP: " + remoteIp + @"</div>
        <p>To gain access, you need to log in from an authorized IP address.<br>
        If this is your first time logging in, please check the server console for the initial password.</p>
        <button onclick='location.reload()'>Try Again</button>
    </div>
</body>
</html>");
                    return;
                }
                
                _logger.LogDebug($"Admin access granted from IP: {remoteIp}");
            }
            
            await _next(context);
        }
    }
}