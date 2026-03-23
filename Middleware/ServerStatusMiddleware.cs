using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PackArcade2.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PackArcade2.Middleware
{
    public class ServerStatusMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ServerStatusMiddleware> _logger;
        private static FileSystemWatcher? _fileWatcher;
        private static readonly string _packArcadePath = @"D:\PackArcade";
        private static bool _isWatching = false;
        private static string? _currentExePath;

        // File extensions to IGNORE (generated/media files)
        private static readonly string[] _ignoredExtensions = new[]
        {
            // Video files
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
            // Image files
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp", ".ico", ".svg",
            // Audio files
            ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a",
            // Generated/temp files
            ".tmp", ".lock", ".log", ".cache"
        };

        public ServerStatusMiddleware(RequestDelegate next, ILogger<ServerStatusMiddleware> logger)
        {
            _next = next;
            _logger = logger;
            
            // Get current executable path
            if (string.IsNullOrEmpty(_currentExePath))
            {
                _currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
            }
            
            // Initialize file watcher only once
            if (!_isWatching)
            {
                InitializeFileWatcher();
                _isWatching = true;
            }
        }

        private void InitializeFileWatcher()
        {
            try
            {
                // Ensure directory exists
                if (!Directory.Exists(_packArcadePath))
                {
                    _logger.LogWarning($"Directory not found: {_packArcadePath}");
                    return;
                }
                
                // Watch the entire PackArcade directory for changes
                _fileWatcher = new FileSystemWatcher(_packArcadePath)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                
                // Filter for all files
                _fileWatcher.Filter = "*.*";
                
                _fileWatcher.Changed += OnFileChanged;
                _fileWatcher.Created += OnFileChanged;
                _fileWatcher.Deleted += OnFileChanged;
                _fileWatcher.Renamed += OnFileRenamed;
                
                _logger.LogInformation($"File watcher initialized - monitoring {_packArcadePath} and all subdirectories (logging only, no restarts)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize file watcher");
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Skip temporary files, lock files, and system files
                var fileName = Path.GetFileName(e.FullPath);
                if (fileName.StartsWith("~") || 
                    fileName.StartsWith(".") || 
                    fileName.EndsWith(".tmp") || 
                    fileName.EndsWith(".lock") ||
                    e.FullPath.Contains("\\obj\\") ||
                    e.FullPath.Contains("\\bin\\") ||
                    e.FullPath.Contains("\\.vs\\") ||
                    e.FullPath.Contains("\\node_modules\\"))
                {
                    return;
                }
                
                // Skip video and media files
                var ext = Path.GetExtension(e.FullPath).ToLower();
                if (_ignoredExtensions.Contains(ext))
                {
                    return;
                }
                
                // Get relative path for logging
                string relativePath = e.FullPath.Replace(_packArcadePath, "").TrimStart('\\');
                
                // Just log the change - NO RESTART
                _logger.LogInformation($"📁 File change detected: {relativePath} ({e.ChangeType})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging file change");
            }
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                // Skip video and media files
                var ext = Path.GetExtension(e.FullPath).ToLower();
                if (_ignoredExtensions.Contains(ext))
                {
                    return;
                }
                
                // Get relative path for logging
                string relativePath = e.FullPath.Replace(_packArcadePath, "").TrimStart('\\');
                string oldRelativePath = e.OldFullPath.Replace(_packArcadePath, "").TrimStart('\\');
                
                // Just log the rename - NO RESTART
                _logger.LogInformation($"📁 File renamed: {oldRelativePath} -> {relativePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging file rename");
            }
        }

        public async Task InvokeAsync(HttpContext context, ServerStatusService statusService)
        {
            // Don't redirect if already on server-offline page or if it's an API request
            if (!context.Request.Path.StartsWithSegments("/server-offline") &&
                !context.Request.Path.StartsWithSegments("/api") &&
                !context.Request.Path.StartsWithSegments("/_blazor") &&
                !context.Request.Path.StartsWithSegments("/css") &&
                !context.Request.Path.StartsWithSegments("/js") &&
                !context.Request.Path.StartsWithSegments("/lib"))
            {
                var status = statusService.GetCurrentStatus();
                
                // If server is in offline window, redirect to offline page
                if (status.InOfflineWindow)
                {
                    _logger.LogInformation($"Redirecting to offline page - {status.OfflineReason}");
                    context.Response.Redirect("/server-offline");
                    return;
                }
            }

            await _next(context);
        }
        
        public static void DisposeWatcher()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }
        }
    }
}