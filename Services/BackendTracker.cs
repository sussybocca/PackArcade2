using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

public class BackendTracker
{
    private readonly InMemoryConfigProvider _configProvider;
    private readonly Dictionary<string, BackendInfo> _backends = new();
    private readonly ILogger<BackendTracker> _logger;

    public BackendTracker(InMemoryConfigProvider configProvider, ILogger<BackendTracker> logger)
    {
        _configProvider = configProvider;
        _logger = logger;
    }

    public void RegisterBackend(string subdomain, int port, string framework)
    {
        _backends[subdomain] = new BackendInfo 
        { 
            Port = port, 
            Framework = framework,
            StartedAt = DateTime.Now 
        };
        
        _logger.LogInformation($"Registering backend for {subdomain} on port {port} ({framework})");
        UpdateRoutes();
    }

    public void UnregisterBackend(string subdomain)
    {
        if (_backends.Remove(subdomain))
        {
            _logger.LogInformation($"Unregistering backend for {subdomain}");
            UpdateRoutes();
        }
    }

    private void UpdateRoutes()
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var (subdomain, backend) in _backends)
        {
            var clusterId = $"cluster-{subdomain}";
            
            // Add cluster - REMOVED the Timeout property that doesn't exist
            clusters.Add(new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["backend"] = new DestinationConfig
                    {
                        Address = $"http://localhost:{backend.Port}"
                    }
                }
                // HttpRequest property removed - not available in this version
            });

            // Add route
            routes.Add(new RouteConfig
            {
                RouteId = $"route-{subdomain}",
                ClusterId = clusterId,
                Match = new RouteMatch
                {
                    Hosts = new[] { $"{subdomain}.packarcade.win" },
                    Path = "/{**catch-all}"
                }
                // Transforms removed - not needed for basic proxying
            });

            _logger.LogDebug($"Added route for {subdomain}.packarcade.win -> localhost:{backend.Port}");
        }

        _configProvider.Update(routes, clusters);
    }

    public BackendInfo? GetBackend(string subdomain)
    {
        return _backends.TryGetValue(subdomain, out var backend) ? backend : null;
    }

    public IReadOnlyDictionary<string, BackendInfo> GetAllBackends() => _backends;

    public class BackendInfo
    {
        public int Port { get; set; }
        public string Framework { get; set; } = "";
        public DateTime StartedAt { get; set; }
    }
}