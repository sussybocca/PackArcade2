using System.Diagnostics;

public class ServerStatusService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ServerStatusService> _logger;
    private DateTime _processStartTime;
    private Process? _serverProcess;

    public ServerStatusService(IWebHostEnvironment env, ILogger<ServerStatusService> logger)
    {
        _env = env;
        _logger = logger;
        
        // Get the current process (your server)
        _serverProcess = Process.GetCurrentProcess();
        _processStartTime = _serverProcess.StartTime;
    }

    public ServerStatus GetCurrentStatus()
    {
        var now = DateTime.Now;
        var hour = now.Hour;
        var minute = now.Minute;
        var currentTime = now.ToString("hh:mm tt");
        
        // Default to online
        bool isRunning = true;
        string statusMessage = "Online";
        string statusColor = "success";
        string statusIcon = "✅";
        bool isInOfflineWindow = false;
        string offlineReason = "";
        DateTime? estimatedReturn = null;
        
        // Check if process is actually alive - THIS IS THE ONLY THING THAT TRIGGERS OFFLINE
        try
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                isRunning = false;
                statusMessage = "Offline - Server process terminated";
                statusColor = "danger";
                statusIcon = "❌";
            }
        }
        catch
        {
            isRunning = false;
            statusMessage = "Offline - Unknown error";
            statusColor = "danger";
            statusIcon = "❌";
        }
        
        // Calculate uptime (only if running)
        string uptimeString = "N/A";
        if (isRunning)
        {
            var uptime = now - _processStartTime;
            uptimeString = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        }
        
        return new ServerStatus
        {
            IsOnline = isRunning,
            StatusMessage = statusMessage,
            StatusColor = statusColor,
            StatusIcon = statusIcon,
            CurrentTime = currentTime,
            Uptime = uptimeString,
            StartTime = _processStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
            InOfflineWindow = false, // Removed schedule-based offline
            OfflineReason = "",
            EstimatedReturn = "Unknown"
        };
    }
}

public class ServerStatus
{
    public bool IsOnline { get; set; }
    public string StatusMessage { get; set; } = "";
    public string StatusColor { get; set; } = "";
    public string StatusIcon { get; set; } = "";
    public string CurrentTime { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string StartTime { get; set; } = "";
    public bool InOfflineWindow { get; set; }
    public string OfflineReason { get; set; } = "";
    public string EstimatedReturn { get; set; } = "";
}