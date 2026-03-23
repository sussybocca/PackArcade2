// Middleware/PointsMiddleware.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PackArcade2.Services;
using System.Threading.Tasks;

namespace PackArcade2.Middleware
{
    public class PointsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<PointsMiddleware> _logger;

        public PointsMiddleware(RequestDelegate next, ILogger<PointsMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, PointsService pointsService)
        {
            var path = context.Request.Path.Value ?? "";
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            
            if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                clientIp = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            }
            
            // Check API Key for embedding
            if (context.Request.Headers.ContainsKey("X-API-Key"))
            {
                var apiKey = context.Request.Headers["X-API-Key"].ToString();
                if (pointsService.ValidateApiKey(apiKey, out var keyData))
                {
                    _logger.LogInformation($"API request from {keyData.OwnerIP} using key {apiKey}");
                    await _next(context);
                    return;
                }
            }
            
            // Check project expiry for editor routes
            if (path.StartsWith("/editor/"))
            {
                var projectName = path.Replace("/editor/", "").Split('/')[0];
                if (pointsService.IsProjectExpired(projectName))
                {
                    context.Response.StatusCode = 410; // Gone
                    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head><title>Project Expired</title></head>
<body>
    <h1>⏰ Project Expired</h1>
    <p>This project has expired. Use points to extend its lifespan!</p>
    <p>Cost: 50 points per day</p>
    <a href=""/points"">🔋 Extend with Points</a>
    <a href=""/editor"">✨ Create New Project</a>
</body>
</html>");
                    return;
                }
                
                // Check if project is frozen (read-only)
                if (context.Request.Method != "GET" && pointsService.IsProjectFrozen(projectName))
                {
                    context.Response.StatusCode = 403; // Forbidden
                    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head><title>Project Frozen</title></head>
<body>
    <h1>❄️ Project is Frozen (Read-Only)</h1>
    <p>This project has been frozen. You can view it, but cannot make changes.</p>
    <p>Use points to unfreeze the project!</p>
    <a href=""/points"">🔓 Unfreeze with Points</a>
</body>
</html>");
                    return;
                }
            }
            
            // Check scheduled runs
            if (path.StartsWith("/api/run/scheduled") && context.Request.Method == "POST")
            {
                // This endpoint would be called by the scheduled runner
                // No points deduction here - it's the execution of a scheduled run
                await _next(context);
                return;
            }
            
            // Check points for other services
            var requiredPoints = GetRequiredPoints(path);
            
            if (requiredPoints > 0)
            {
                var reduction = pointsService.GetTimeReduction(clientIp);
                
                if (reduction.RemainingToday <= TimeSpan.Zero)
                {
                    context.Response.StatusCode = 429;
                    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head><title>Time Limit Reached</title></head>
<body>
    <h1>⏰ Time Limit Reached</h1>
    <p>You have used all your available time for today.</p>
    <p>Time resets at: " + reduction.NextReset.ToString("HH:mm") + @"</p>
    <p>Play games to earn more points and reduce your time penalty!</p>
    <a href=""/runner"">🎮 Play Games</a>
</body>
</html>");
                    return;
                }
                
                var user = pointsService.GetUserPoints(clientIp);
                if (user.Points < requiredPoints)
                {
                    context.Response.StatusCode = 402;
                    await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head><title>Insufficient Points</title></head>
<body>
    <h1>🎮 Insufficient Points</h1>
    <p>You need " + requiredPoints + @" points to access this feature.</p>
    <p>Current points: " + user.Points + @"</p>
    <p>Play games to earn more points!</p>
    <a href=""/runner"">🎮 Play Games</a>
</body>
</html>");
                    return;
                }
                
                var success = await pointsService.UseService(clientIp, GetServiceName(path));
                if (!success)
                {
                    context.Response.StatusCode = 402;
                    await context.Response.WriteAsync("Insufficient points for this action.");
                    return;
                }
                
                _logger.LogInformation($"User {clientIp} used {requiredPoints} points for {path}");
            }
            
            await _next(context);
        }
        
        private int GetRequiredPoints(string path)
        {
            if (path.StartsWith("/editor") && !path.Contains("new") && !path.Contains("list")) return 10;
            if (path.StartsWith("/ai")) return 5;
            if (path.StartsWith("/runner")) return 2;
            if (path.StartsWith("/api/admin/backup")) return 100;
            if (path.StartsWith("/api/points/extend")) return 50;
            if (path.StartsWith("/api/points/freeze")) return 20;
            if (path.StartsWith("/api/points/transfer")) return 0; // Free, but transfers cost points
            if (path.StartsWith("/api/points/schedule")) return 100;
            if (path.StartsWith("/api/points/generate-key")) return 500;
            return 0;
        }
        
        private string GetServiceName(string path)
        {
            if (path.StartsWith("/editor")) return "editor";
            if (path.StartsWith("/ai")) return "ai";
            if (path.StartsWith("/runner")) return "runner";
            if (path.StartsWith("/api/admin/backup")) return "server_backup";
            return "unknown";
        }
    }
}