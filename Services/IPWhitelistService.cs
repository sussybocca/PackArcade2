using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PackArcade2.Services
{
    public class IPWhitelistService
    {
        private readonly ConcurrentDictionary<string, DateTime> _allowedIPs = new();
        private readonly string _whitelistFile;
        private readonly ILogger<IPWhitelistService> _logger;
        private readonly object _fileLock = new();

        public IPWhitelistService(ILogger<IPWhitelistService> logger)
        {
            _logger = logger;
            _whitelistFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "whitelist.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_whitelistFile));
            Load();
        }

        public void AddIP(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            
            _allowedIPs[ip] = DateTime.UtcNow;
            Save();
            _logger.LogInformation($"IP {ip} added to whitelist");
        }

        public void RemoveIP(string ip)
        {
            _allowedIPs.TryRemove(ip, out _);
            Save();
            _logger.LogInformation($"IP {ip} removed from whitelist");
        }

        public bool IsAllowed(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            return _allowedIPs.ContainsKey(ip);
        }
public bool HasAnyAllowedIPs()
{
    return _allowedIPs.Any();
}
        public void Reset()
        {
            _allowedIPs.Clear();
            Save();
            _logger.LogWarning("IP whitelist has been reset");
        }

        public List<string> GetAllowedIPs()
        {
            return _allowedIPs.Keys.ToList();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_whitelistFile))
                {
                    var json = File.ReadAllText(_whitelistFile);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            _allowedIPs[kvp.Key] = kvp.Value;
                        }
                    }
                    _logger.LogInformation($"Loaded {_allowedIPs.Count} IPs from whitelist");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load IP whitelist");
            }
        }

        private void Save()
        {
            lock (_fileLock)
            {
                try
                {
                    var dict = _allowedIPs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_whitelistFile, json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save IP whitelist");
                }
            }
        }
    }
}