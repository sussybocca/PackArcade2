using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Diagnostics;
using System.Text;

namespace PackArcade2.Services
{
    public class PointsService
    {
        private readonly ILogger<PointsService> _logger;
        private readonly ConcurrentDictionary<string, UserPoints> _userPoints = new();
        private readonly ConcurrentDictionary<string, UserTimeUsage> _userTimeUsage = new();
        private readonly ConcurrentDictionary<string, ProjectLifespan> _projectLifespans = new();
        private readonly ConcurrentDictionary<string, ProjectFreeze> _projectFreezes = new();
        private readonly ConcurrentDictionary<string, ApiKey> _apiKeys = new();
        private readonly ConcurrentDictionary<string, ScheduledRun> _scheduledRuns = new();
        private readonly string _dataPath = @"D:\PackArcade\Data\Points";
        private readonly string _projectsPath = @"D:\PackArcade\wwwroot\projects";
        private readonly string _logPath = @"D:\PackArcade\Data\Points\RunLogs";

        public PointsService(ILogger<PointsService> logger)
        {
            _logger = logger;
            Directory.CreateDirectory(_dataPath);
            Directory.CreateDirectory(Path.Combine(_dataPath, "Projects"));
            Directory.CreateDirectory(Path.Combine(_dataPath, "ApiKeys"));
            Directory.CreateDirectory(Path.Combine(_dataPath, "ScheduledRuns"));
            Directory.CreateDirectory(_logPath);
            LoadAllUserData();
            LoadAllProjectData();
            LoadApiKeys();
            LoadScheduledRuns();
            
            // Start background task to process scheduled runs
            Task.Run(ProcessScheduledRuns);
        }

        private async Task ProcessScheduledRuns()
        {
            while (true)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var runsToExecute = _scheduledRuns.Values
                        .Where(r => r.Status == "Pending" && r.ScheduledAt <= now)
                        .ToList();

                    foreach (var run in runsToExecute)
                    {
                        await ExecuteScheduledRun(run);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing scheduled runs");
                }
                
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        private async Task ExecuteScheduledRun(ScheduledRun run)
        {
            try
            {
                _logger.LogInformation($"Executing scheduled run {run.Id} for project {run.ProjectName}");
                
                run.Status = "Running";
                SaveScheduledRun(run.Id);
                
                var output = new StringBuilder();
                var projectPath = Path.Combine(_projectsPath, run.ProjectName);
                var logFile = Path.Combine(_logPath, $"{run.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                
                if (!Directory.Exists(projectPath))
                {
                    run.Status = "Failed";
                    run.Output = $"Project directory not found: {projectPath}";
                    run.CompletedAt = DateTime.UtcNow;
                    SaveScheduledRun(run.Id);
                    await File.WriteAllTextAsync(logFile, run.Output);
                    _logger.LogError(run.Output);
                    return;
                }
                
                string command = "";
                string arguments = "";
                
                // Detect project type and set command
                if (File.Exists(Path.Combine(projectPath, "package.json")))
                {
                    command = "npm";
                    arguments = "start";
                    output.AppendLine("Detected Node.js project");
                }
                else if (File.Exists(Path.Combine(projectPath, "server.py")))
                {
                    command = "python";
                    arguments = "server.py";
                    output.AppendLine("Detected Python project");
                }
                else if (File.Exists(Path.Combine(projectPath, "Program.cs")))
                {
                    command = "dotnet";
                    arguments = "run";
                    output.AppendLine("Detected C# project");
                }
                else if (File.Exists(Path.Combine(projectPath, "index.php")))
                {
                    command = "php";
                    arguments = "-S localhost:8000";
                    output.AppendLine("Detected PHP project");
                }
                else if (File.Exists(Path.Combine(projectPath, "server.js")))
                {
                    command = "node";
                    arguments = "server.js";
                    output.AppendLine("Detected Node.js server");
                }
                else
                {
                    run.Status = "Failed";
                    run.Output = "No executable project found (package.json, server.py, Program.cs, index.php, or server.js required)";
                    run.CompletedAt = DateTime.UtcNow;
                    SaveScheduledRun(run.Id);
                    await File.WriteAllTextAsync(logFile, run.Output);
                    return;
                }
                
                output.AppendLine($"Starting server in background for project: {run.ProjectName}");
                output.AppendLine($"Command: {command} {arguments}");
                output.AppendLine($"Working directory: {projectPath}");
                
                // Start the server process in the background
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    WorkingDirectory = projectPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                var process = new Process { StartInfo = psi };
                process.Start();
                
                // Capture output for logging
                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                
                output.AppendLine($"Process started with PID: {process.Id}");
                output.AppendLine($"Output:");
                output.AppendLine(stdout);
                if (!string.IsNullOrEmpty(stderr))
                {
                    output.AppendLine($"Errors:");
                    output.AppendLine(stderr);
                }
                
                // The server is now running in the background
                run.Output = output.ToString();
                run.Status = "Running";
                run.ProcessId = process.Id;
                run.CompletedAt = null; // Still running
                SaveScheduledRun(run.Id);
                
                await File.WriteAllTextAsync(logFile, output.ToString());
                _logger.LogInformation($"Scheduled run {run.Id} started server with PID {process.Id}");
                
                // Monitor the process and update status when it exits
                _ = Task.Run(async () =>
                {
                    await process.WaitForExitAsync();
                    run.Status = process.ExitCode == 0 ? "Completed" : "Failed";
                    run.CompletedAt = DateTime.UtcNow;
                    run.Output += $"\n\nServer stopped with exit code: {process.ExitCode}";
                    SaveScheduledRun(run.Id);
                    _logger.LogInformation($"Server for run {run.Id} stopped with exit code {process.ExitCode}");
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing scheduled run {run.Id}");
                run.Status = "Failed";
                run.Output = $"Execution error: {ex.Message}";
                run.CompletedAt = DateTime.UtcNow;
                SaveScheduledRun(run.Id);
            }
        }

        private string SanitizeFileName(string key)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = key;
            foreach (var c in invalidChars)
            {
                sanitized = sanitized.Replace(c.ToString(), "_");
            }
            return sanitized;
        }

        private void LoadAllUserData()
        {
            try
            {
                var files = Directory.GetFiles(_dataPath, "user_*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var userPoints = JsonSerializer.Deserialize<UserPoints>(json);
                        if (userPoints != null)
                        {
                            _userPoints[SanitizeFileName(userPoints.IP)] = userPoints;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to load user data from {file}");
                    }
                }
                _logger.LogInformation($"Loaded {_userPoints.Count} user point records");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user data");
            }
        }

        private void LoadAllProjectData()
        {
            var projectsPath = Path.Combine(_dataPath, "Projects");
            if (Directory.Exists(projectsPath))
            {
                foreach (var file in Directory.GetFiles(projectsPath, "lifespan_*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var lifespan = JsonSerializer.Deserialize<ProjectLifespan>(json);
                        if (lifespan != null)
                        {
                            _projectLifespans[lifespan.ProjectName] = lifespan;
                        }
                    }
                    catch { }
                }
                
                foreach (var file in Directory.GetFiles(projectsPath, "freeze_*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var freeze = JsonSerializer.Deserialize<ProjectFreeze>(json);
                        if (freeze != null)
                        {
                            _projectFreezes[freeze.ProjectName] = freeze;
                        }
                    }
                    catch { }
                }
            }
        }

        private void LoadApiKeys()
        {
            var apiKeysPath = Path.Combine(_dataPath, "ApiKeys");
            if (Directory.Exists(apiKeysPath))
            {
                foreach (var file in Directory.GetFiles(apiKeysPath, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var apiKey = JsonSerializer.Deserialize<ApiKey>(json);
                        if (apiKey != null)
                        {
                            _apiKeys[apiKey.Key] = apiKey;
                        }
                    }
                    catch { }
                }
            }
        }

        private void LoadScheduledRuns()
        {
            var runsPath = Path.Combine(_dataPath, "ScheduledRuns");
            if (Directory.Exists(runsPath))
            {
                foreach (var file in Directory.GetFiles(runsPath, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var run = JsonSerializer.Deserialize<ScheduledRun>(json);
                        if (run != null)
                        {
                            _scheduledRuns[run.Id] = run;
                        }
                    }
                    catch { }
                }
            }
        }

        public UserPoints GetUserPoints(string ip, string? userId = null)
        {
            var key = SanitizeFileName(userId ?? ip);
            if (!_userPoints.ContainsKey(key))
            {
                _userPoints[key] = new UserPoints
                {
                    IP = ip,
                    UserId = userId,
                    Points = 100,
                    TotalPointsEarned = 0,
                    TotalPointsSpent = 0,
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                };
                SaveUserPoints(key);
            }
            return _userPoints[key];
        }

        public async Task<bool> ExtendProjectLifespan(string ip, string projectName, int additionalDays, string? userId = null)
        {
            var cost = additionalDays * 50;
            var user = GetUserPoints(ip, userId);
            
            if (user.Points < cost)
                return false;
            
            if (!_projectLifespans.ContainsKey(projectName))
            {
                _projectLifespans[projectName] = new ProjectLifespan
                {
                    ProjectName = projectName,
                    OwnerIP = ip,
                    OwnerId = userId,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(30),
                    BaseDays = 30,
                    ExtendedDays = 0
                };
            }
            
            var lifespan = _projectLifespans[projectName];
            lifespan.ExtendedDays += additionalDays;
            lifespan.ExpiresAt = lifespan.ExpiresAt.AddDays(additionalDays);
            lifespan.LastExtended = DateTime.UtcNow;
            
            var success = await SpendPoints(ip, cost, $"Extend project '{projectName}' by {additionalDays} days", userId);
            
            if (success)
            {
                SaveProjectLifespan(projectName);
                _logger.LogInformation($"Project '{projectName}' lifespan extended by {additionalDays} days. New expiry: {lifespan.ExpiresAt}");
                
                // Create a marker file to indicate extended lifespan
                var markerFile = Path.Combine(_projectsPath, projectName, ".lifespan_extended");
                await File.WriteAllTextAsync(markerFile, $"Extended by {additionalDays} days on {DateTime.UtcNow}");
            }
            
            return success;
        }

        public DateTime? GetProjectExpiry(string projectName)
        {
            if (_projectLifespans.TryGetValue(projectName, out var lifespan))
                return lifespan.ExpiresAt;
            return null;
        }

        public bool IsProjectExpired(string projectName)
        {
            if (_projectLifespans.TryGetValue(projectName, out var lifespan))
                return lifespan.ExpiresAt < DateTime.UtcNow;
            return false;
        }

        public async Task<bool> FreezeProject(string ip, string projectName, int days, string? userId = null)
        {
            var cost = days * 20;
            var user = GetUserPoints(ip, userId);
            
            if (user.Points < cost)
                return false;
            
            _projectFreezes[projectName] = new ProjectFreeze
            {
                ProjectName = projectName,
                OwnerIP = ip,
                OwnerId = userId,
                FrozenUntil = DateTime.UtcNow.AddDays(days),
                IsFrozen = true,
                FrozenAt = DateTime.UtcNow
            };
            
            var success = await SpendPoints(ip, cost, $"Freeze project '{projectName}' for {days} days", userId);
            
            if (success)
            {
                SaveProjectFreeze(projectName);
                _logger.LogInformation($"Project '{projectName}' frozen until {_projectFreezes[projectName].FrozenUntil}");
                
                // Create marker file
                var freezeFile = Path.Combine(_projectsPath, projectName, ".frozen");
                await File.WriteAllTextAsync(freezeFile, $"Frozen until {_projectFreezes[projectName].FrozenUntil}");
            }
            
            return success;
        }

        public bool IsProjectFrozen(string projectName)
        {
            if (_projectFreezes.TryGetValue(projectName, out var freeze))
            {
                if (freeze.FrozenUntil < DateTime.UtcNow)
                {
                    _projectFreezes.TryRemove(projectName, out _);
                    return false;
                }
                return freeze.IsFrozen;
            }
            return false;
        }

        public void UnfreezeProject(string projectName)
        {
            _projectFreezes.TryRemove(projectName, out _);
            var freezePath = Path.Combine(_dataPath, "Projects", $"freeze_{projectName}.json");
            if (File.Exists(freezePath))
                File.Delete(freezePath);
            
            var markerFile = Path.Combine(_projectsPath, projectName, ".frozen");
            if (File.Exists(markerFile))
                File.Delete(markerFile);
        }

        public async Task<string> GenerateApiKey(string ip, string? userId = null)
        {
            var cost = 500;
            var user = GetUserPoints(ip, userId);
            
            if (user.Points < cost)
                return "";
            
            var apiKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("+", "").Replace("/", "").Replace("=", "")[..32];
            
            _apiKeys[apiKey] = new ApiKey
            {
                Key = apiKey,
                OwnerIP = ip,
                OwnerId = userId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                RateLimit = 1000,
                RequestsToday = 0,
                LastReset = DateTime.UtcNow
            };
            
            var success = await SpendPoints(ip, cost, "Generate API Key", userId);
            
            if (success)
            {
                SaveApiKey(apiKey);
                _logger.LogInformation($"API Key generated for {ip}");
            }
            
            return success ? apiKey : "";
        }

        public bool ValidateApiKey(string apiKey, out ApiKey? key)
        {
            key = null;
            if (!_apiKeys.TryGetValue(apiKey, out var apiKeyData))
                return false;
            
            if (apiKeyData.ExpiresAt < DateTime.UtcNow)
            {
                _apiKeys.TryRemove(apiKey, out _);
                return false;
            }
            
            if (apiKeyData.LastReset.Date < DateTime.UtcNow.Date)
            {
                apiKeyData.RequestsToday = 0;
                apiKeyData.LastReset = DateTime.UtcNow;
            }
            
            if (apiKeyData.RequestsToday >= apiKeyData.RateLimit)
                return false;
            
            apiKeyData.RequestsToday++;
            key = apiKeyData;
            SaveApiKey(apiKey);
            return true;
        }

        public async Task<bool> ScheduleRun(string ip, string projectName, DateTime runAt, string? userId = null)
        {
            var cost = 100;
            var user = GetUserPoints(ip, userId);
            
            if (user.Points < cost)
                return false;
            
            var projectPath = Path.Combine(_projectsPath, projectName);
            if (!Directory.Exists(projectPath))
            {
                _logger.LogWarning($"Project {projectName} not found for scheduling");
                return false;
            }
            
            var runId = Guid.NewGuid().ToString();
            _scheduledRuns[runId] = new ScheduledRun
            {
                Id = runId,
                ProjectName = projectName,
                OwnerIP = ip,
                OwnerId = userId,
                ScheduledAt = runAt,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            };
            
            var success = await SpendPoints(ip, cost, $"Schedule run for '{projectName}'", userId);
            
            if (success)
            {
                SaveScheduledRun(runId);
                _logger.LogInformation($"Scheduled run {runId} for {projectName} at {runAt}");
            }
            
            return success;
        }

        public List<ScheduledRun> GetUserScheduledRuns(string ip, string? userId = null)
        {
            var key = userId ?? ip;
            return _scheduledRuns.Values
                .Where(r => r.OwnerIP == ip || r.OwnerId == key)
                .OrderBy(r => r.ScheduledAt)
                .ToList();
        }

        public List<ScheduledRun> GetPendingScheduledRuns(DateTime currentTime)
        {
            return _scheduledRuns.Values
                .Where(r => r.Status == "Pending" && r.ScheduledAt <= currentTime)
                .ToList();
        }

        public void UpdateScheduledRun(ScheduledRun run)
        {
            _scheduledRuns[run.Id] = run;
            SaveScheduledRun(run.Id);
        }

        public async Task<bool> TransferPoints(string fromIp, string toIp, int points, string? fromUserId = null, string? toUserId = null)
        {
            if (points <= 0)
                return false;
            
            var fromUser = GetUserPoints(fromIp, fromUserId);
            if (fromUser.Points < points)
                return false;
            
            var toUser = GetUserPoints(toIp, toUserId);
            
            fromUser.Points -= points;
            fromUser.TotalPointsSpent += points;
            fromUser.Transactions.Add(new PointTransaction
            {
                Amount = -points,
                Type = "Transfer Out",
                Source = $"Transfer to {toIp}",
                Timestamp = DateTime.UtcNow
            });
            SaveUserPoints(SanitizeFileName(fromUserId ?? fromIp));
            
            toUser.Points += points;
            toUser.TotalPointsEarned += points;
            toUser.Transactions.Add(new PointTransaction
            {
                Amount = points,
                Type = "Transfer In",
                Source = $"Transfer from {fromIp}",
                Timestamp = DateTime.UtcNow
            });
            SaveUserPoints(SanitizeFileName(toUserId ?? toIp));
            
            _logger.LogInformation($"Transferred {points} points from {fromIp} to {toIp}");
            return true;
        }

        public async Task<int> AddPoints(string ip, int points, string source, string? userId = null)
        {
            var key = SanitizeFileName(userId ?? ip);
            var user = GetUserPoints(ip, userId);
            user.Points += points;
            user.TotalPointsEarned += points;
            user.LastUpdated = DateTime.UtcNow;
            
            user.Transactions.Add(new PointTransaction
            {
                Amount = points,
                Type = "Earn",
                Source = source,
                Timestamp = DateTime.UtcNow
            });
            
            SaveUserPoints(key);
            _logger.LogInformation($"Added {points} points to {key} from {source}. New balance: {user.Points}");
            
            await UpdateLeaderboard(key, user.Points);
            
            return user.Points;
        }

        public async Task<bool> SpendPoints(string ip, int points, string purpose, string? userId = null)
        {
            var key = SanitizeFileName(userId ?? ip);
            var user = GetUserPoints(ip, userId);
            
            if (user.Points < points)
                return false;
            
            user.Points -= points;
            user.TotalPointsSpent += points;
            user.LastUpdated = DateTime.UtcNow;
            
            user.Transactions.Add(new PointTransaction
            {
                Amount = -points,
                Type = "Spend",
                Source = purpose,
                Timestamp = DateTime.UtcNow
            });
            
            SaveUserPoints(key);
            _logger.LogInformation($"Spent {points} points from {key} on {purpose}. New balance: {user.Points}");
            
            return true;
        }

        public TimeReduction GetTimeReduction(string ip, string? userId = null)
        {
            var key = userId ?? ip;
            var user = GetUserPoints(ip, userId);
            var usage = GetUserTimeUsage(ip, userId);
            
            double reductionMultiplier = 1.0;
            if (user.TotalPointsSpent > 0)
            {
                reductionMultiplier = Math.Max(0.5, 1.0 - (user.TotalPointsSpent / 100000.0));
            }
            
            var todayStart = DateTime.Today;
            var usedToday = usage.GetUsedTimeToday();
            var adjustedLimit = TimeSpan.FromHours(24 * reductionMultiplier);
            var remaining = adjustedLimit - usedToday;
            
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            
            return new TimeReduction
            {
                ReductionMultiplier = reductionMultiplier,
                DailyLimit = adjustedLimit,
                UsedToday = usedToday,
                RemainingToday = remaining,
                PointsSpentTotal = user.TotalPointsSpent,
                NextReset = todayStart.AddDays(1)
            };
        }

        public async Task<bool> UseService(string ip, string serviceName)
        {
            var user = GetUserPoints(ip);
            
            var requiredPoints = serviceName switch
            {
                "editor" => 10,
                "ai" => 5,
                "runner" => 2,
                "server_backup" => 100,
                _ => 0
            };
            
            if (requiredPoints > 0 && user.Points < requiredPoints)
                return false;
            
            if (requiredPoints > 0)
            {
                user.Points -= requiredPoints;
                user.TotalPointsSpent += requiredPoints;
                user.Transactions.Add(new PointTransaction
                {
                    Amount = -requiredPoints,
                    Type = "Spend",
                    Source = serviceName,
                    Timestamp = DateTime.UtcNow
                });
                SaveUserPoints(SanitizeFileName(ip));
                _logger.LogInformation($"Used {requiredPoints} points for {serviceName} from {ip}. New balance: {user.Points}");
            }
            
            var usage = GetUserTimeUsage(ip);
            var duration = TimeSpan.FromMinutes(10);
            usage.AddUsage(serviceName, duration);
            SaveUserTimeUsage(SanitizeFileName(ip));
            
            return true;
        }

        private UserTimeUsage GetUserTimeUsage(string ip, string? userId = null)
        {
            var key = SanitizeFileName(userId ?? ip);
            if (!_userTimeUsage.ContainsKey(key))
            {
                _userTimeUsage[key] = new UserTimeUsage
                {
                    IP = ip,
                    UserId = userId,
                    UsageHistory = new List<ServiceUsage>()
                };
                LoadUserTimeUsage(key);
            }
            return _userTimeUsage[key];
        }

        private void LoadUserTimeUsage(string key)
        {
            var filePath = Path.Combine(_dataPath, $"usage_{key}.json");
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var usage = JsonSerializer.Deserialize<UserTimeUsage>(json);
                    if (usage != null)
                    {
                        _userTimeUsage[key] = usage;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to load usage for {key}");
                }
            }
        }

        private void SaveUserTimeUsage(string key)
        {
            try
            {
                key = SanitizeFileName(key);
                var filePath = Path.Combine(_dataPath, $"usage_{key}.json");
                var json = JsonSerializer.Serialize(_userTimeUsage[key], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save usage for {key}");
            }
        }

        private void SaveUserPoints(string key)
        {
            try
            {
                key = SanitizeFileName(key);
                var filePath = Path.Combine(_dataPath, $"user_{key}.json");
                var json = JsonSerializer.Serialize(_userPoints[key], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save points for {key}");
            }
        }

        private void SaveProjectLifespan(string projectName)
        {
            try
            {
                var filePath = Path.Combine(_dataPath, "Projects", $"lifespan_{projectName}.json");
                var json = JsonSerializer.Serialize(_projectLifespans[projectName], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save project lifespan for {projectName}");
            }
        }

        private void SaveProjectFreeze(string projectName)
        {
            try
            {
                var filePath = Path.Combine(_dataPath, "Projects", $"freeze_{projectName}.json");
                var json = JsonSerializer.Serialize(_projectFreezes[projectName], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save project freeze for {projectName}");
            }
        }

        private void SaveApiKey(string apiKey)
        {
            try
            {
                var filePath = Path.Combine(_dataPath, "ApiKeys", $"{apiKey}.json");
                var json = JsonSerializer.Serialize(_apiKeys[apiKey], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save API key");
            }
        }

        private void SaveScheduledRun(string runId)
        {
            try
            {
                var filePath = Path.Combine(_dataPath, "ScheduledRuns", $"{runId}.json");
                var json = JsonSerializer.Serialize(_scheduledRuns[runId], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save scheduled run");
            }
        }

        private async Task UpdateLeaderboard(string key, int points)
        {
            var leaderboardPath = Path.Combine(_dataPath, "leaderboard.json");
            try
            {
                Dictionary<string, int> leaderboard;
                if (File.Exists(leaderboardPath))
                {
                    var json = await File.ReadAllTextAsync(leaderboardPath);
                    leaderboard = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
                }
                else
                {
                    leaderboard = new();
                }
                
                leaderboard[key] = points;
                var top50 = leaderboard.OrderByDescending(x => x.Value).Take(50)
                    .ToDictionary(x => x.Key, x => x.Value);
                
                var newJson = JsonSerializer.Serialize(top50, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(leaderboardPath, newJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update leaderboard");
            }
        }

        public Dictionary<string, int> GetLeaderboard()
        {
            var leaderboardPath = Path.Combine(_dataPath, "leaderboard.json");
            try
            {
                if (File.Exists(leaderboardPath))
                {
                    var json = File.ReadAllText(leaderboardPath);
                    return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load leaderboard");
            }
            return new();
        }
    }

    public class UserPoints
    {
        public string IP { get; set; } = "";
        public string? UserId { get; set; }
        public int Points { get; set; }
        public int TotalPointsEarned { get; set; }
        public int TotalPointsSpent { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<PointTransaction> Transactions { get; set; } = new();
    }

    public class PointTransaction
    {
        public int Amount { get; set; }
        public string Type { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    public class UserTimeUsage
    {
        public string IP { get; set; } = "";
        public string? UserId { get; set; }
        public List<ServiceUsage> UsageHistory { get; set; } = new();

        public TimeSpan GetUsedTimeToday()
        {
            var today = DateTime.Today;
            return TimeSpan.FromMinutes(UsageHistory
                .Where(u => u.Timestamp >= today)
                .Sum(u => u.Duration.TotalMinutes));
        }

        public void AddUsage(string service, TimeSpan duration)
        {
            UsageHistory.Add(new ServiceUsage
            {
                Service = service,
                Duration = duration,
                Timestamp = DateTime.UtcNow
            });
            
            UsageHistory = UsageHistory
                .Where(u => u.Timestamp > DateTime.UtcNow.AddDays(-30))
                .ToList();
        }
    }

    public class ServiceUsage
    {
        public string Service { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TimeReduction
    {
        public double ReductionMultiplier { get; set; }
        public TimeSpan DailyLimit { get; set; }
        public TimeSpan UsedToday { get; set; }
        public TimeSpan RemainingToday { get; set; }
        public int PointsSpentTotal { get; set; }
        public DateTime NextReset { get; set; }
    }

    public class ProjectLifespan
    {
        public string ProjectName { get; set; } = "";
        public string OwnerIP { get; set; } = "";
        public string? OwnerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int BaseDays { get; set; }
        public int ExtendedDays { get; set; }
        public DateTime? LastExtended { get; set; }
    }

    public class ProjectFreeze
    {
        public string ProjectName { get; set; } = "";
        public string OwnerIP { get; set; } = "";
        public string? OwnerId { get; set; }
        public DateTime FrozenUntil { get; set; }
        public bool IsFrozen { get; set; }
        public DateTime FrozenAt { get; set; }
    }

    public class ApiKey
    {
        public string Key { get; set; } = "";
        public string OwnerIP { get; set; } = "";
        public string? OwnerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public int RateLimit { get; set; }
        public int RequestsToday { get; set; }
        public DateTime LastReset { get; set; }
    }

    public class ScheduledRun
    {
        public string Id { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string OwnerIP { get; set; } = "";
        public string? OwnerId { get; set; }
        public DateTime ScheduledAt { get; set; }
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public int? ProcessId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}