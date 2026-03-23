using System;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Security.Cryptography;
using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Runtime.InteropServices;
using System.Net.WebSockets;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PackArcade2.Services
{
    public class ServerConnectionService : BackgroundService
    {
        private readonly ILogger<ServerConnectionService> _logger;
        private readonly ConcurrentDictionary<string, UserServer> _userServers = new();
        private readonly ConcurrentDictionary<string, ServerProcess> _runningServers = new();
        private readonly ConcurrentDictionary<string, WebSocket> _activeAgentConnections = new();
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _agentFileWatchers = new();
        private readonly ConcurrentDictionary<string, List<string>> _pendingFileUpdates = new();
        private readonly string _serversPath = @"D:\PackArcade\Servers";
        private readonly string _tempDeployPath = @"D:\PackArcade\Temp\Deployments";
        private readonly string _sandboxPath = @"D:\PackArcade\Sandbox\ServerSandbox";
        private readonly string _quarantinePath = @"D:\PackArcade\Servers\Quarantine";
        private readonly string _serverStoragePath = @"F:\ServerStorage";
        private readonly string _malwareDbPath = @"D:\PackArcade\Data\MalwareDB";
        private readonly ConcurrentQueue<string> _scanQueue = new();
        private static readonly object _deployLock = new object();
        private readonly BackendTracker _backendTracker;

        public ServerConnectionService(ILogger<ServerConnectionService> logger, BackendTracker backendTracker)
        {
            _logger = logger;
            _backendTracker = backendTracker;
            
            Directory.CreateDirectory(_serversPath);
            Directory.CreateDirectory(_tempDeployPath);
            Directory.CreateDirectory(_sandboxPath);
            Directory.CreateDirectory(_quarantinePath);
            Directory.CreateDirectory(_serverStoragePath);
            Directory.CreateDirectory(_malwareDbPath);
            
            LoadServers();
            
            _logger.LogInformation($"Server Connection Service initialized. Storage path: {_serverStoragePath}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Server Connection Service background scanner started");
            
            while (!stoppingToken.IsCancellationRequested)
            {
                // Process malware scan queue
                if (_scanQueue.TryDequeue(out var serverId))
                {
                    await PerformFullMalwareScan(serverId, stoppingToken);
                }
                
                // Monitor running servers
                await MonitorRunningServers(stoppingToken);
                
                // Process pending file updates from agents
                await ProcessPendingFileUpdates(stoppingToken);
                
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        private async Task ProcessPendingFileUpdates(CancellationToken stoppingToken)
        {
            foreach (var kvp in _pendingFileUpdates.ToList())
            {
                var serverId = kvp.Key;
                var files = kvp.Value;
                
                if (_userServers.TryGetValue(serverId, out var server))
                {
                    foreach (var filePath in files)
                    {
                        _logger.LogInformation($"Processing file update for server {serverId}: {filePath}");
                        // File updates are handled by the agent connection
                    }
                }
                
                _pendingFileUpdates.TryRemove(serverId, out _);
            }
            
            await Task.CompletedTask;
        }

        private async Task MonitorRunningServers(CancellationToken stoppingToken)
        {
            foreach (var kvp in _runningServers.ToList())
            {
                var serverId = kvp.Key;
                var serverProcess = kvp.Value;
                
                if (serverProcess.Process == null || serverProcess.Process.HasExited)
                {
                    _logger.LogWarning($"Server {serverId} process died unexpectedly");
                    _runningServers.TryRemove(serverId, out _);
                    
                    // Update server status
                    if (_userServers.TryGetValue(serverId, out var server))
                    {
                        server.Status = "Stopped";
                        server.ErrorMessage = "Process crashed";
                        SaveServer(serverId);
                        
                        // Unregister from reverse proxy
                        try
                        {
                            _backendTracker.UnregisterBackend(serverId);
                        }
                        catch { }
                    }
                }
                else if (serverProcess.Process != null)
                {
                    // Update last heartbeat
                    serverProcess.LastHeartbeat = DateTime.UtcNow;
                }
            }
            
            await Task.CompletedTask;
        }

        private void LoadServers()
        {
            try
            {
                if (!Directory.Exists(_serversPath)) return;
                
                var serverFiles = Directory.GetFiles(_serversPath, "server_*.json");
                foreach (var file in serverFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var server = JsonSerializer.Deserialize<UserServer>(json);
                        if (server != null && !string.IsNullOrEmpty(server.ServerId))
                        {
                            _userServers[server.ServerId] = server;
                            
                            if (server.ScanStatus == "Pending" || server.ScanStatus == "Scanning")
                            {
                                _scanQueue.Enqueue(server.ServerId);
                            }
                            
                            // Auto-start servers that were running before restart
                            if (server.Status == "Running" && server.AutoStart)
                            {
                                _ = Task.Run(async () => await StartServerProcess(server.ServerId));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to load server from {file}");
                    }
                }
                _logger.LogInformation($"Loaded {_userServers.Count} user servers");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load servers");
            }
        }

        public async Task<DeploymentResult> DeployServer(string apiKey, string serverName, byte[] serverFiles, string configJson)
        {
            var serverId = Guid.NewGuid().ToString();
            var tempDeployFolder = Path.Combine(_tempDeployPath, serverId);
            
            try
            {
                _logger.LogInformation($"Starting deployment for server {serverName} (ID: {serverId})");
                
                Directory.CreateDirectory(tempDeployFolder);
                
                var zipPath = Path.Combine(tempDeployFolder, "server_files.zip");
                await File.WriteAllBytesAsync(zipPath, serverFiles);
                ZipFile.ExtractToDirectory(zipPath, tempDeployFolder);
                File.Delete(zipPath);
                
                var configPath = Path.Combine(tempDeployFolder, "config.json");
                await File.WriteAllTextAsync(configPath, configJson);
                
                _logger.LogInformation($"Files extracted to {tempDeployFolder}");
                
                var quickScanResult = await QuickMalwareScan(tempDeployFolder);
                if (!quickScanResult.IsClean)
                {
                    _logger.LogWarning($"Malware detected in server {serverId}: {quickScanResult.Detections}");
                    Directory.Delete(tempDeployFolder, true);
                    return new DeploymentResult 
                    { 
                        Success = false, 
                        Message = $"Malware detected: {quickScanResult.Detections}. Server blocked.",
                        RequiresScan = true
                    };
                }
                
                var sandboxResult = await TestInSandbox(tempDeployFolder);
                if (!sandboxResult.Success)
                {
                    _logger.LogWarning($"Sandbox test failed for server {serverId}: {sandboxResult.Message}");
                    Directory.Delete(tempDeployFolder, true);
                    return new DeploymentResult 
                    { 
                        Success = false, 
                        Message = $"Sandbox test failed: {sandboxResult.Message}",
                        RequiresManualReview = true
                    };
                }
                
                string serverFolder = Path.Combine(_serversPath, serverId);
                Directory.Move(tempDeployFolder, serverFolder);
                
                var config = JsonSerializer.Deserialize<ServerConfig>(configJson) ?? new ServerConfig();
                
                var server = new UserServer
                {
                    ServerId = serverId,
                    ServerName = serverName,
                    ApiKey = apiKey,
                    DeployedAt = DateTime.UtcNow,
                    Status = "Deployed",
                    ScanStatus = "Queued",
                    ScanStartedAt = DateTime.UtcNow,
                    FolderPath = serverFolder,
                    Config = config,
                    AutoStart = true,
                    Port = config.Port > 0 ? config.Port : 3000
                };
                
                _userServers[serverId] = server;
                SaveServer(serverId);
                
                _scanQueue.Enqueue(serverId);
                
                // Generate both server script and agent script
                string scriptContent = await GenerateServerConnectScript(serverId, serverName, configJson);
                string agentScriptContent = await GenerateAgentConnectScript(serverId, serverName);
                
                // Auto-start the server if it passed basic checks
                await StartServerProcess(serverId);
                
                _logger.LogInformation($"Server {serverId} deployed and started. Queued for security scan.");
                
                return new DeploymentResult
                {
                    Success = true,
                    ServerId = serverId,
                    Message = "Server deployed and started. Security scan in progress.",
                    ScriptContent = scriptContent,
                    AgentScriptContent = agentScriptContent,
                    Status = "Running",
                    ServerUrl = $"https://{serverId}.packarcade.win"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Deployment failed for server {serverId}");
                
                if (Directory.Exists(tempDeployFolder))
                {
                    try { Directory.Delete(tempDeployFolder, true); } catch { }
                }
                
                return new DeploymentResult
                {
                    Success = false,
                    Message = $"Deployment failed: {ex.Message}"
                };
            }
        }

        private async Task<string> GenerateAgentConnectScript(string serverId, string serverName)
        {
            var baseUrl = "https://packarcade.win";
            var wsUrl = $"wss://packarcade.win/api/agent/connect";
            
            var scriptBuilder = new StringBuilder();
            scriptBuilder.AppendLine("<#");
            scriptBuilder.AppendLine(".SYNOPSIS");
            scriptBuilder.AppendLine("    PackArcade2 Agent - Connect your local server for live updates");
            scriptBuilder.AppendLine(".DESCRIPTION");
            scriptBuilder.AppendLine("    This script establishes a secure WebSocket connection to PackArcade2");
            scriptBuilder.AppendLine("    enabling real-time file sync, live updates, and advanced features.");
            scriptBuilder.AppendLine("    It requires administrator privileges to run.");
            scriptBuilder.AppendLine(".NOTES");
            scriptBuilder.AppendLine($"    Server ID: {serverId}");
            scriptBuilder.AppendLine($"    Server Name: {serverName}");
            scriptBuilder.AppendLine($"    Created: {DateTime.UtcNow}");
            scriptBuilder.AppendLine("#>");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("#Requires -RunAsAdministrator");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"  PACKARCADE2 AGENT - LIVE CONNECTION\" -ForegroundColor Green");
            scriptBuilder.AppendLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine($"$ServerId = \"{serverId}\"");
            scriptBuilder.AppendLine($"$ServerName = \"{serverName}\"");
            scriptBuilder.AppendLine($"$BaseUrl = \"{baseUrl}\"");
            scriptBuilder.AppendLine($"$WsUrl = \"{wsUrl}\"");
            scriptBuilder.AppendLine($"$LocalPath = \"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\PackArcade2\\Servers\\{serverId}\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Server ID: $ServerId\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Server Name: $ServerName\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Connecting to PackArcade2 cloud...\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Local sync path: $LocalPath\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
            scriptBuilder.AppendLine("if (-not $isAdmin) {");
            scriptBuilder.AppendLine("    Write-Host \"[ERROR] This script must be run as Administrator!\" -ForegroundColor Red");
            scriptBuilder.AppendLine("    Write-Host \"Please right-click PowerShell and select 'Run as Administrator'\" -ForegroundColor Red");
            scriptBuilder.AppendLine("    exit 1");
            scriptBuilder.AppendLine("}");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("# Create local directory if it doesn't exist");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Creating local directory: $LocalPath\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("New-Item -ItemType Directory -Path $LocalPath -Force -ErrorAction SilentlyContinue | Out-Null");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("# Download initial server files");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Downloading server files...\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("$FilesUrl = \"$BaseUrl/api/server-connection/download/$ServerId\"");
            scriptBuilder.AppendLine("try {");
            scriptBuilder.AppendLine("    $zipPath = \"$env:TEMP\\server_files_$ServerId.zip\"");
            scriptBuilder.AppendLine("    Invoke-WebRequest -Uri $FilesUrl -OutFile $zipPath -UseBasicParsing");
            scriptBuilder.AppendLine("    Expand-Archive -Path $zipPath -DestinationPath $LocalPath -Force");
            scriptBuilder.AppendLine("    Remove-Item $zipPath -Force");
            scriptBuilder.AppendLine("    Write-Host \"[INFO] Files downloaded and extracted\" -ForegroundColor Green");
            scriptBuilder.AppendLine("} catch {");
            scriptBuilder.AppendLine("    Write-Host \"[WARN] Could not download initial files: $_\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("}");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("# Function to sync files to cloud");
            scriptBuilder.AppendLine("function Sync-ToCloud {");
            scriptBuilder.AppendLine("    param([string]$FilePath)");
            scriptBuilder.AppendLine("    try {");
            scriptBuilder.AppendLine("        $relativePath = $FilePath.Substring($LocalPath.Length + 1)");
            scriptBuilder.AppendLine("        $bytes = [System.IO.File]::ReadAllBytes($FilePath)");
            scriptBuilder.AppendLine("        $base64 = [Convert]::ToBase64String($bytes)");
            scriptBuilder.AppendLine("        $payload = @{");
            scriptBuilder.AppendLine("            serverId = $ServerId");
            scriptBuilder.AppendLine("            path = $relativePath");
            scriptBuilder.AppendLine("            content = $base64");
            scriptBuilder.AppendLine("            timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')");
            scriptBuilder.AppendLine("        } | ConvertTo-Json");
            scriptBuilder.AppendLine("        $response = Invoke-RestMethod -Uri \"$BaseUrl/api/agent/sync\" -Method POST -Body $payload -ContentType \"application/json\"");
            scriptBuilder.AppendLine("        if ($response.success) {");
            scriptBuilder.AppendLine("            Write-Host \"[SYNC] Uploaded: $relativePath\" -ForegroundColor Green");
            scriptBuilder.AppendLine("        }");
            scriptBuilder.AppendLine("    } catch {");
            scriptBuilder.AppendLine("        Write-Host \"[SYNC ERROR] Failed to upload $FilePath: $_\" -ForegroundColor Red");
            scriptBuilder.AppendLine("    }");
            scriptBuilder.AppendLine("}");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("# Function to download files from cloud");
            scriptBuilder.AppendLine("function Sync-FromCloud {");
            scriptBuilder.AppendLine("    param([string]$Path, [string]$Content)");
            scriptBuilder.AppendLine("    try {");
            scriptBuilder.AppendLine("        $fullPath = Join-Path $LocalPath $Path");
            scriptBuilder.AppendLine("        $directory = Split-Path $fullPath -Parent");
            scriptBuilder.AppendLine("        if (!(Test-Path $directory)) {");
            scriptBuilder.AppendLine("            New-Item -ItemType Directory -Path $directory -Force | Out-Null");
            scriptBuilder.AppendLine("        }");
            scriptBuilder.AppendLine("        $bytes = [Convert]::FromBase64String($Content)");
            scriptBuilder.AppendLine("        [System.IO.File]::WriteAllBytes($fullPath, $bytes)");
            scriptBuilder.AppendLine("        Write-Host \"[SYNC] Downloaded: $Path\" -ForegroundColor Green");
            scriptBuilder.AppendLine("    } catch {");
            scriptBuilder.AppendLine("        Write-Host \"[SYNC ERROR] Failed to download $Path: $_\" -ForegroundColor Red");
            scriptBuilder.AppendLine("    }");
            scriptBuilder.AppendLine("}");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("# Set up file system watcher for live updates");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Setting up file watcher for live updates...\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("$watcher = New-Object System.IO.FileSystemWatcher");
            scriptBuilder.AppendLine("$watcher.Path = $LocalPath");
            scriptBuilder.AppendLine("$watcher.IncludeSubdirectories = $true");
            scriptBuilder.AppendLine("$watcher.EnableRaisingEvents = $true");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("$action = {");
            scriptBuilder.AppendLine("    $path = $Event.SourceEventArgs.FullPath");
            scriptBuilder.AppendLine("    $changeType = $Event.SourceEventArgs.ChangeType");
            scriptBuilder.AppendLine("    Write-Host \"[FS] $changeType detected: $path\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("    if ($changeType -eq 'Changed' -or $changeType -eq 'Created') {");
            scriptBuilder.AppendLine("        Start-Sleep -Milliseconds 500");
            scriptBuilder.AppendLine("        Sync-ToCloud -FilePath $path");
            scriptBuilder.AppendLine("    }");
            scriptBuilder.AppendLine("}");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Register-ObjectEvent $watcher 'Changed' -Action $action | Out-Null");
            scriptBuilder.AppendLine("Register-ObjectEvent $watcher 'Created' -Action $action | Out-Null");
            scriptBuilder.AppendLine("Register-ObjectEvent $watcher 'Deleted' -Action {");
            scriptBuilder.AppendLine("    $path = $Event.SourceEventArgs.FullPath");
            scriptBuilder.AppendLine("    Write-Host \"[FS] Deleted: $path\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("    $relativePath = $path.Substring($LocalPath.Length + 1)");
            scriptBuilder.AppendLine("    try {");
            scriptBuilder.AppendLine("        $payload = @{ serverId = $ServerId; path = $relativePath } | ConvertTo-Json");
            scriptBuilder.AppendLine("        Invoke-RestMethod -Uri \"$BaseUrl/api/agent/delete\" -Method POST -Body $payload -ContentType \"application/json\"");
            scriptBuilder.AppendLine("    } catch { }");
            scriptBuilder.AppendLine("} | Out-Null");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"========================================\" -ForegroundColor Green");
            scriptBuilder.AppendLine("Write-Host \"  AGENT CONNECTED!\" -ForegroundColor Green");
            scriptBuilder.AppendLine("========================================\" -ForegroundColor Green");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"Your local server is now connected to PackArcade2.\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"File changes will be synced automatically.\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"Press Ctrl+C to disconnect and stop the agent.\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("# Keep the script running");
            scriptBuilder.AppendLine("try {");
            scriptBuilder.AppendLine("    while ($true) {");
            scriptBuilder.AppendLine("        Start-Sleep -Seconds 10");
            scriptBuilder.AppendLine("        Write-Host \"[HEARTBEAT] Agent is connected and syncing...\" -ForegroundColor DarkGray");
            scriptBuilder.AppendLine("    }");
            scriptBuilder.AppendLine("} finally {");
            scriptBuilder.AppendLine("    $watcher.EnableRaisingEvents = $false");
            scriptBuilder.AppendLine("    $watcher.Dispose()");
            scriptBuilder.AppendLine("    Write-Host \"[INFO] Agent disconnected\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("}");
            
            var scriptContent = scriptBuilder.ToString();
            var scriptPath = Path.Combine(_tempDeployPath, $"{serverId}_Agent-Connect.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptContent);
            return scriptContent;
        }

        public async Task<bool> RegisterAgentConnection(string serverId, WebSocket webSocket)
        {
            if (!_userServers.TryGetValue(serverId, out var server))
            {
                _logger.LogWarning($"Agent connection attempt for unknown server: {serverId}");
                return false;
            }
            
            if (_activeAgentConnections.ContainsKey(serverId))
            {
                try { await _activeAgentConnections[serverId].CloseAsync(WebSocketCloseStatus.NormalClosure, "Replaced by new connection", CancellationToken.None); } catch { }
                _activeAgentConnections.TryRemove(serverId, out _);
            }
            
            _activeAgentConnections[serverId] = webSocket;
            _logger.LogInformation($"Agent connected for server {serverId}");
            
            // Start listening for messages
            _ = Task.Run(async () => await HandleAgentMessages(serverId, webSocket));
            
            return true;
        }
        
        private async Task HandleAgentMessages(string serverId, WebSocket webSocket)
        {
            var buffer = new byte[1024 * 1024]; // 1MB buffer
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                        break;
                    }
                    
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogInformation($"Received from agent {serverId}: {message}");
                    
                    // Process sync messages
                    if (message.StartsWith("SYNC:"))
                    {
                        var parts = message.Substring(5).Split('|');
                        if (parts.Length >= 2)
                        {
                            var filePath = parts[0];
                            var content = parts[1];
                            
                            if (_userServers.TryGetValue(serverId, out var server))
                            {
                                var fullPath = Path.Combine(server.FolderPath, filePath);
                                var dir = Path.GetDirectoryName(fullPath);
                                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                var bytes = Convert.FromBase64String(content);
                                await File.WriteAllBytesAsync(fullPath, bytes);
                                _logger.LogInformation($"Synced file from agent: {filePath}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling agent messages for server {serverId}");
            }
            finally
            {
                _activeAgentConnections.TryRemove(serverId, out _);
                _logger.LogInformation($"Agent disconnected for server {serverId}");
            }
        }
        
        public async Task<bool> SyncFileToAgent(string serverId, string filePath, byte[] content)
        {
            if (!_activeAgentConnections.TryGetValue(serverId, out var webSocket))
                return false;
            
            if (webSocket.State != WebSocketState.Open)
                return false;
            
            try
            {
                var message = $"SYNC:{filePath}|{Convert.ToBase64String(content)}";
                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to sync file to agent {serverId}");
                return false;
            }
        }

        public async Task<bool> StartServerProcess(string serverId)
        {
            if (!_userServers.TryGetValue(serverId, out var server))
                return false;
            
            if (_runningServers.ContainsKey(serverId))
            {
                _logger.LogWarning($"Server {serverId} is already running");
                return true;
            }
            
            try
            {
                _logger.LogInformation($"Starting server process for {serverId}");
                
                string command = "";
                string arguments = "";
                string workingDir = server.FolderPath;
                
                // Detect project type and set command
                if (File.Exists(Path.Combine(workingDir, "server.js")))
                {
                    // First, install dependencies if package.json exists
                    if (File.Exists(Path.Combine(workingDir, "package.json")))
                    {
                        _logger.LogInformation($"Installing npm dependencies for server {serverId}...");
                        var npmInstallPsi = new ProcessStartInfo
                        {
                            FileName = @"D:\npm\npm.cmd",
                            Arguments = "install",
                            WorkingDirectory = workingDir,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        
                        // Set PATH for npm install too
                        npmInstallPsi.EnvironmentVariables["PATH"] = @"D:\npm;" + (Environment.GetEnvironmentVariable("PATH") ?? "");
                        
                        var npmProcess = Process.Start(npmInstallPsi);
                        if (npmProcess != null)
                        {
                            string output = await npmProcess.StandardOutput.ReadToEndAsync();
                            string error = await npmProcess.StandardError.ReadToEndAsync();
                            await npmProcess.WaitForExitAsync();
                            
                            if (!string.IsNullOrEmpty(output)) _logger.LogInformation(output);
                            if (!string.IsNullOrEmpty(error)) _logger.LogWarning(error);
                        }
                    }
                    
                    command = @"D:\npm\node.exe";
                    arguments = "server.js";
                    _logger.LogInformation($"Detected Node.js server file for server {serverId}");
                }
                else if (File.Exists(Path.Combine(workingDir, "package.json")))
                {
                    command = @"D:\npm\npm.cmd";
                    arguments = "start";
                    _logger.LogInformation($"Detected Node.js project (package.json) for server {serverId}");
                }
                else if (File.Exists(Path.Combine(workingDir, "server.py")))
                {
                    command = "python";
                    arguments = "server.py";
                    _logger.LogInformation($"Detected Python project for server {serverId}");
                }
                else if (File.Exists(Path.Combine(workingDir, "Program.cs")))
                {
                    command = "dotnet";
                    arguments = "run";
                    _logger.LogInformation($"Detected C# project for server {serverId}");
                }
                else if (File.Exists(Path.Combine(workingDir, "index.php")))
                {
                    command = "php";
                    arguments = $"-S 0.0.0.0:{server.Port}";
                    _logger.LogInformation($"Detected PHP project for server {serverId}");
                }
                else if (!string.IsNullOrEmpty(server.Config.StartCommand))
                {
                    var parts = server.Config.StartCommand.Split(' ', 2);
                    command = parts[0];
                    arguments = parts.Length > 1 ? parts[1] : "";
                    _logger.LogInformation($"Using custom start command for server {serverId}: {command} {arguments}");
                }
                else
                {
                    _logger.LogError($"No executable found for server {serverId}");
                    server.Status = "Error";
                    server.ErrorMessage = "No executable found (server.js, package.json, server.py, Program.cs, index.php, or custom start command required)";
                    SaveServer(serverId);
                    return false;
                }
                
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Add Node.js to PATH for this specific process
                psi.EnvironmentVariables["PATH"] = @"D:\npm;" + (Environment.GetEnvironmentVariable("PATH") ?? "");
                psi.EnvironmentVariables["PORT"] = server.Port.ToString();
                psi.EnvironmentVariables["ASPNETCORE_URLS"] = $"http://*:{server.Port}";
                psi.EnvironmentVariables["NODE_ENV"] = "production";
                
                var process = new Process { StartInfo = psi };
                process.Start();
                
                // Create Windows Job Object for sandboxing
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                        PriorityClass = BELOW_NORMAL_PRIORITY_CLASS
                    },
                    ProcessMemoryLimit = new UIntPtr((ulong)512 * 1024 * 1024) // 512MB limit
                };
                
                SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits));
                AssignProcessToJobObject(job, process.Handle);
                
                // Capture output for logging
                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogInformation($"[Server {serverId}] {e.Data}");
                        // Store last few lines for debugging
                        var lastOutput = server.LastOutput ?? new List<string>();
                        lastOutput.Add($"[{DateTime.Now:HH:mm:ss}] {e.Data}");
                        if (lastOutput.Count > 100) lastOutput.RemoveAt(0);
                        server.LastOutput = lastOutput;
                        SaveServer(serverId);
                    }
                };
                
                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogError($"[Server {serverId} ERROR] {e.Data}");
                        var lastOutput = server.LastOutput ?? new List<string>();
                        lastOutput.Add($"[{DateTime.Now:HH:mm:ss}] ERROR: {e.Data}");
                        if (lastOutput.Count > 100) lastOutput.RemoveAt(0);
                        server.LastOutput = lastOutput;
                        SaveServer(serverId);
                    }
                };
                
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                _runningServers[serverId] = new ServerProcess
                {
                    Process = process,
                    JobHandle = job,
                    StartTime = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow,
                    Port = server.Port
                };
                
                server.Status = "Running";
                server.Pid = process.Id;
                server.StartedAt = DateTime.UtcNow;
                server.ErrorMessage = null;
                SaveServer(serverId);
                
                // Register with reverse proxy
                try
                {
                    _backendTracker.RegisterBackend(serverId, server.Port, server.Config.GetFramework());
                    _logger.LogInformation($"Server {serverId} registered with reverse proxy on port {server.Port}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to register server {serverId} with reverse proxy");
                }
                
                // Monitor process exit
                _ = Task.Run(async () =>
                {
                    await Task.Run(() => process.WaitForExit());
                    _logger.LogWarning($"Server {serverId} process exited with code {process.ExitCode}");
                    _runningServers.TryRemove(serverId, out _);
                    
                    if (_userServers.TryGetValue(serverId, out var srv))
                    {
                        srv.Status = "Stopped";
                        srv.ExitCode = process.ExitCode;
                        SaveServer(serverId);
                    }
                    
                    try
                    {
                        _backendTracker.UnregisterBackend(serverId);
                    }
                    catch { }
                    
                    if (job != IntPtr.Zero)
                    {
                        TerminateJobObject(job, 1);
                    }
                });
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start server {serverId}");
                server.Status = "Error";
                server.ErrorMessage = ex.Message;
                SaveServer(serverId);
                return false;
            }
        }
        
        public async Task<bool> StopServerProcess(string serverId)
        {
            if (!_runningServers.TryGetValue(serverId, out var serverProcess))
            {
                _logger.LogWarning($"Server {serverId} is not running");
                return true;
            }
            
            try
            {
                _logger.LogInformation($"Stopping server process for {serverId}");
                
                if (serverProcess.JobHandle != IntPtr.Zero)
                {
                    TerminateJobObject(serverProcess.JobHandle, 1);
                }
                
                if (serverProcess.Process != null && !serverProcess.Process.HasExited)
                {
                    serverProcess.Process.Kill();
                    await Task.Run(() => serverProcess.Process.WaitForExit(10000));
                }
                
                _runningServers.TryRemove(serverId, out _);
                
                if (_userServers.TryGetValue(serverId, out var server))
                {
                    server.Status = "Stopped";
                    SaveServer(serverId);
                }
                
                try
                {
                    _backendTracker.UnregisterBackend(serverId);
                }
                catch { }
                
                _logger.LogInformation($"Server {serverId} stopped successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to stop server {serverId}");
                return false;
            }
        }
        
        public async Task<bool> RestartServerProcess(string serverId)
        {
            await StopServerProcess(serverId);
            await Task.Delay(2000);
            return await StartServerProcess(serverId);
        }
        
        public ServerStatus GetServerStatus(string serverId)
        {
            if (!_userServers.TryGetValue(serverId, out var server))
                return null;
            
            var isRunning = _runningServers.ContainsKey(serverId);
            
            return new ServerStatus
            {
                ServerId = serverId,
                ServerName = server.ServerName,
                Status = server.Status,
                IsRunning = isRunning,
                Pid = server.Pid,
                Port = server.Port,
                StartedAt = server.StartedAt,
                Uptime = isRunning && server.StartedAt.HasValue ? DateTime.UtcNow - server.StartedAt.Value : null,
                LastOutput = server.LastOutput?.TakeLast(20).ToList() ?? new List<string>(),
                ErrorMessage = server.ErrorMessage,
                HasAgent = _activeAgentConnections.ContainsKey(serverId)
            };
        }

        private async Task<string> GenerateServerConnectScript(string serverId, string serverName, string configJson)
        {
            var baseUrl = "https://packarcade.win";
            var escapedConfigJson = configJson.Replace("'", "''");
            
            var scriptBuilder = new StringBuilder();
            scriptBuilder.AppendLine("<#");
            scriptBuilder.AppendLine(".SYNOPSIS");
            scriptBuilder.AppendLine("    Server-Connect - Deploy and manage your server on PackArcade2");
            scriptBuilder.AppendLine(".DESCRIPTION");
            scriptBuilder.AppendLine("    This script deploys your server to PackArcade2 infrastructure.");
            scriptBuilder.AppendLine("    Your server will run on our cloud with a dedicated subdomain.");
            scriptBuilder.AppendLine(".NOTES");
            scriptBuilder.AppendLine($"    Server ID: {serverId}");
            scriptBuilder.AppendLine($"    Server Name: {serverName}");
            scriptBuilder.AppendLine($"    Created: {DateTime.UtcNow}");
            scriptBuilder.AppendLine("#>");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"  PACKARCADE2 SERVER-CONNECT DEPLOYMENT\" -ForegroundColor Green");
            scriptBuilder.AppendLine("Write-Host \"========================================\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine($"$ServerId = \"{serverId}\"");
            scriptBuilder.AppendLine($"$ServerName = \"{serverName}\"");
            scriptBuilder.AppendLine($"$BaseUrl = \"{baseUrl}\"");
            scriptBuilder.AppendLine($"$ServerUrl = \"https://{serverId}.packarcade.win\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Server ID: $ServerId\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Server Name: $ServerName\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Your server will be deployed to PackArcade2 cloud\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Access your server at: $ServerUrl\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Uploading server files...\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Security scan in progress...\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"[INFO] Server will start automatically after passing security checks\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("");
            scriptBuilder.AppendLine("Write-Host \"========================================\" -ForegroundColor Green");
            scriptBuilder.AppendLine("Write-Host \"  DEPLOYMENT INITIATED!\" -ForegroundColor Green");
            scriptBuilder.AppendLine("========================================\" -ForegroundColor Green");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine($"Write-Host \"Your server will be available at: $ServerUrl\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"The server will start automatically after security scan.\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"Check back in a few minutes.\" -ForegroundColor Yellow");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Write-Host \"For live file updates, run the Agent-Connect.ps1 script on your local machine.\" -ForegroundColor Cyan");
            scriptBuilder.AppendLine("Write-Host \"\"");
            scriptBuilder.AppendLine("Read-Host \"Press Enter to exit\"");
            
            var scriptContent = scriptBuilder.ToString();
            var scriptPath = Path.Combine(_tempDeployPath, $"{serverId}_Server-Connect.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptContent);
            return scriptContent;
        }

        // ... rest of existing methods (QuickMalwareScan, TestInSandbox, PerformFullMalwareScan, DeepScanFolder, etc.) remain exactly as they were ...
        private async Task<ScanResult> QuickMalwareScan(string folderPath)
        {
            var result = new ScanResult { IsClean = true, Detections = "" };
            var suspiciousPatterns = new[]
            {
                "CreateObject", "WScript.Shell", "Shell.Application", "Exec",
                "Delete", "Format", "RegDelete", "net user", "net localgroup",
                "powershell -", "cmd /c", "Start-Process", "Invoke-Expression"
            };
            
            var skipExtensions = new[] { ".html", ".htm", ".mp3", ".mp4", ".wav", ".jpg", ".png", ".gif", ".css", ".js", ".json" };
            
            try
            {
                var allFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                foreach (var file in allFiles)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    
                    if (skipExtensions.Contains(ext))
                    {
                        continue;
                    }
                    
                    var content = await File.ReadAllTextAsync(file);
                    foreach (var pattern in suspiciousPatterns)
                    {
                        if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            result.IsClean = false;
                            result.Detections = $"Suspicious pattern '{pattern}' found in {Path.GetFileName(file)}";
                            _logger.LogWarning(result.Detections);
                            return result;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quick scan error");
                result.IsClean = false;
                result.Detections = $"Scan error: {ex.Message}";
            }
            
            return result;
        }

        private async Task<SandboxResult> TestInSandbox(string folderPath)
        {
            var result = new SandboxResult { Success = true, Message = "" };
            var sandboxFolder = Path.Combine(_sandboxPath, Guid.NewGuid().ToString());
            
            try
            {
                Directory.CreateDirectory(sandboxFolder);
                CopyDirectory(folderPath, sandboxFolder);
                
                var scriptFiles = Directory.GetFiles(sandboxFolder, "*.ps1", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(sandboxFolder, "*.bat", SearchOption.AllDirectories))
                    .Concat(Directory.GetFiles(sandboxFolder, "*.cmd", SearchOption.AllDirectories));
                
                foreach (var script in scriptFiles)
                {
                    var content = await File.ReadAllTextAsync(script);
                    if (content.Contains("Delete") && content.Contains("System32"))
                    {
                        result.Success = false;
                        result.Message = $"Suspicious script detected: {Path.GetFileName(script)}";
                        break;
                    }
                }
                
                Directory.Delete(sandboxFolder, true);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Sandbox test failed: {ex.Message}";
                if (Directory.Exists(sandboxFolder))
                {
                    try { Directory.Delete(sandboxFolder, true); } catch { }
                }
            }
            
            await Task.CompletedTask;
            return result;
        }

        private async Task PerformFullMalwareScan(string serverId, CancellationToken cancellationToken)
        {
            if (!_userServers.TryGetValue(serverId, out var server))
                return;
            
            server.ScanStatus = "Scanning";
            SaveServer(serverId);
            
            try
            {
                var deepScanResult = await DeepScanFolder(server.FolderPath, cancellationToken);
                
                if (deepScanResult.IsClean)
                {
                    server.ScanStatus = "Clean";
                    server.Status = "Running";
                    server.ScanCompletedAt = DateTime.UtcNow;
                    _logger.LogInformation($"Server {serverId} passed security scan");
                }
                else
                {
                    server.ScanStatus = "Quarantined";
                    server.Status = "Quarantined";
                    server.ScanMessage = deepScanResult.Detections;
                    _logger.LogWarning($"Server {serverId} quarantined: {deepScanResult.Detections}");
                    
                    await StopServerProcess(serverId);
                    
                    string quarantineFolder = Path.Combine(_quarantinePath, serverId);
                    if (Directory.Exists(server.FolderPath))
                    {
                        Directory.Move(server.FolderPath, quarantineFolder);
                        server.FolderPath = quarantineFolder;
                    }
                }
                
                SaveServer(serverId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Scan failed for server {serverId}");
                server.ScanStatus = "Error";
                server.ScanMessage = ex.Message;
                SaveServer(serverId);
            }
        }

        private async Task<ScanResult> DeepScanFolder(string folderPath, CancellationToken cancellationToken)
        {
            var result = new ScanResult { IsClean = true, Detections = "" };
            var knownMalwareHashes = await LoadKnownMalwareHashes();
            
            try
            {
                var defenderPsi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"Start-MpScan -ScanPath \"{folderPath}\" -ScanType CustomScan -Force",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                var defenderProcess = Process.Start(defenderPsi);
                if (defenderProcess != null)
                {
                    var output = await defenderProcess.StandardOutput.ReadToEndAsync();
                    var error = await defenderProcess.StandardError.ReadToEndAsync();
                    await defenderProcess.WaitForExitAsync(cancellationToken);
                    
                    if (output.Contains("Threat") || output.Contains("Malware") || error.Contains("Threat"))
                    {
                        result.IsClean = false;
                        result.Detections = "Windows Defender detected potential threats";
                        _logger.LogWarning($"Windows Defender found threats in {folderPath}");
                    }
                }
                
                var allFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                
                foreach (var file in allFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var hash = await ComputeFileHash(file);
                    if (knownMalwareHashes.Contains(hash))
                    {
                        result.IsClean = false;
                        result.Detections = $"Known malware detected: {Path.GetFileName(file)} (Hash: {hash})";
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deep scan error");
                result.IsClean = false;
                result.Detections = $"Scan error: {ex.Message}";
            }
            
            return result;
        }

        private async Task<HashSet<string>> LoadKnownMalwareHashes()
        {
            var hashes = new HashSet<string>();
            var hashFile = Path.Combine(_malwareDbPath, "known_malware.txt");
            
            if (File.Exists(hashFile))
            {
                var lines = await File.ReadAllLinesAsync(hashFile);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        hashes.Add(line.Trim().ToLowerInvariant());
                }
            }
            
            return hashes;
        }

        private async Task<string> ComputeFileHash(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = await md5.ComputeHashAsync(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private void CopyDirectory(string source, string destination)
        {
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, destination));
            }
            
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, destination), true);
            }
        }

        private void SaveServer(string serverId)
        {
            try
            {
                var json = JsonSerializer.Serialize(_userServers[serverId], new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(_serversPath, $"server_{serverId}.json"), json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save server {serverId}");
            }
        }

        public List<UserServer> GetUserServers(string apiKey)
        {
            return _userServers.Values.Where(s => s.ApiKey == apiKey).ToList();
        }

        public UserServer? GetServer(string serverId)
        {
            _userServers.TryGetValue(serverId, out var server);
            return server;
        }

        public async Task<bool> UpdateServer(string serverId, byte[] updatedFiles)
        {
            if (!_userServers.TryGetValue(serverId, out var server))
                return false;
            
            var wasRunning = _runningServers.ContainsKey(serverId);
            
            if (wasRunning)
            {
                await StopServerProcess(serverId);
            }
            
            var tempFolder = Path.Combine(_tempDeployPath, Guid.NewGuid().ToString());
            
            try
            {
                Directory.CreateDirectory(tempFolder);
                
                var zipPath = Path.Combine(tempFolder, "update.zip");
                await File.WriteAllBytesAsync(zipPath, updatedFiles);
                ZipFile.ExtractToDirectory(zipPath, tempFolder);
                File.Delete(zipPath);
                
                var scanResult = await QuickMalwareScan(tempFolder);
                if (!scanResult.IsClean)
                {
                    return false;
                }
                
                var backupFolder = Path.Combine(_tempDeployPath, $"{serverId}_backup_{DateTime.Now:yyyyMMddHHmmss}");
                CopyDirectory(server.FolderPath, backupFolder);
                
                foreach (var file in Directory.GetFiles(server.FolderPath))
                {
                    File.Delete(file);
                }
                foreach (var dir in Directory.GetDirectories(server.FolderPath))
                {
                    Directory.Delete(dir, true);
                }
                
                CopyDirectory(tempFolder, server.FolderPath);
                
                Directory.Delete(tempFolder, true);
                
                if (wasRunning)
                {
                    await StartServerProcess(serverId);
                }
                
                _logger.LogInformation($"Server {serverId} updated successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update server {serverId}");
                if (wasRunning)
                {
                    await StartServerProcess(serverId);
                }
                return false;
            }
            finally
            {
                if (Directory.Exists(tempFolder))
                {
                    try { Directory.Delete(tempFolder, true); } catch { }
                }
            }
        }

        public byte[]? GetServerFiles(string serverId)
        {
            if (!_userServers.TryGetValue(serverId, out var server))
                return null;
            
            var tempZip = Path.Combine(Path.GetTempPath(), $"{serverId}.zip");
            
            try
            {
                ZipFile.CreateFromDirectory(server.FolderPath, tempZip);
                return File.ReadAllBytes(tempZip);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get server files for {serverId}");
                return null;
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }
        }

        // Windows Job Object API
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x0100;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    }

    public class UserServer
    {
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public DateTime DeployedAt { get; set; }
        public string Status { get; set; } = "Pending";
        public string ScanStatus { get; set; } = "Pending";
        public DateTime? ScanStartedAt { get; set; }
        public DateTime? ScanCompletedAt { get; set; }
        public string? ScanMessage { get; set; }
        public string FolderPath { get; set; } = "";
        public ServerConfig Config { get; set; } = new();
        public bool AutoStart { get; set; } = true;
        public int Port { get; set; } = 3000;
        public int? Pid { get; set; }
        public DateTime? StartedAt { get; set; }
        public int? ExitCode { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> LastOutput { get; set; } = new();
    }

    public class ServerConfig
    {
        public bool InstallNode { get; set; } = false;
        public bool InstallPython { get; set; } = false;
        public bool InstallPhp { get; set; } = false;
        public List<string> NpmPackages { get; set; } = new();
        public List<string> PipPackages { get; set; } = new();
        public string StartCommand { get; set; } = "";
        public int Port { get; set; } = 3000;
        
        public string GetFramework()
        {
            if (!string.IsNullOrEmpty(StartCommand))
            {
                if (StartCommand.Contains("node")) return "node";
                if (StartCommand.Contains("python")) return "python";
                if (StartCommand.Contains("dotnet")) return "csharp";
                if (StartCommand.Contains("php")) return "php";
            }
            return "custom";
        }
    }

    public class ServerProcess
    {
        public Process? Process { get; set; }
        public IntPtr JobHandle { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public int Port { get; set; }
    }

    public class ServerStatus
    {
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsRunning { get; set; }
        public int? Pid { get; set; }
        public int Port { get; set; }
        public DateTime? StartedAt { get; set; }
        public TimeSpan? Uptime { get; set; }
        public List<string> LastOutput { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public bool HasAgent { get; set; }
    }

    public class DeploymentResult
    {
        public bool Success { get; set; }
        public string? ServerId { get; set; }
        public string? Message { get; set; }
        public string? ScriptContent { get; set; }
        public string? AgentScriptContent { get; set; }
        public bool RequiresScan { get; set; }
        public bool RequiresManualReview { get; set; }
        public string? Status { get; set; }
        public string? ServerUrl { get; set; }
    }

    public class ScanResult
    {
        public bool IsClean { get; set; }
        public string Detections { get; set; } = "";
    }

    public class SandboxResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }
}