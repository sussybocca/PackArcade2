using PackArcade2.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using System.IO;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using PackArcade2.Services;
using System.Text;
using System.Web;
using System.Net.WebSockets;
using System.Threading;

// ========== ADMIN ELEVATION CHECK ==========
if (!IsRunningAsAdministrator())
{
    RestartAsAdministrator();
    return;
}

static bool IsRunningAsAdministrator()
{
    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
    {
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

static void RestartAsAdministrator()
{
    var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? AppDomain.CurrentDomain.FriendlyName;
    var startInfo = new ProcessStartInfo(exePath)
    {
        UseShellExecute = true,
        Verb = "runas"
    };
    
    try
    {
        Process.Start(startInfo);
    }
    catch
    {
        Console.WriteLine("This application requires administrator privileges.");
        Console.WriteLine("Please restart as administrator.");
        Console.ReadKey();
    }
    
    Environment.Exit(0);
}
// ========== END ADMIN ELEVATION ==========

var builder = WebApplication.CreateBuilder(args);

// ========== LOAD CONFIGURATION ==========
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// ========== SERVICE REGISTRATION ==========
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<GameService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddSingleton<ServerStatusService>();
builder.Services.AddSingleton<PointsService>(); // Add PointsService
builder.Services.AddSingleton<PKStorageService>(); // Add PKStorageService for VHD storage
builder.Services.AddHttpClient(); // ADD THIS LINE - registers HttpClient for dependency injection
builder.Services.AddSingleton<ServerConnectionService>(); // Add ServerConnectionService
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerConnectionService>()); // Register as hosted service
// ========== EMAIL SERVICE REGISTRATION ==========
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<AdminSettings>(builder.Configuration.GetSection("Admin"));
builder.Services.AddSingleton<IEmailService, EmailService>();

// ========== ADMIN AUTHENTICATION SERVICES ==========
builder.Services.AddSingleton<IPWhitelistService>();
builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddScoped<AdminAuthenticationService>();

// ========== AUTHENTICATION CONFIGURATION ==========
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/denied";
        options.Cookie.Name = ".PackArcade2.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

// ========== YARP REVERSE PROXY CONFIGURATION ==========
var configProvider = new InMemoryConfigProvider(new List<RouteConfig>(), new List<ClusterConfig>());
builder.Services.AddSingleton<IProxyConfigProvider>(configProvider);
builder.Services.AddReverseProxy();
builder.Services.AddSingleton<BackendTracker>(sp => 
    new BackendTracker(configProvider, sp.GetRequiredService<ILogger<BackendTracker>>()));

var app = builder.Build();

// ========== LOCALAPPS PROTOCOL REGISTRATION ==========
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    try
    {
        if (!Registry.ClassesRoot.GetSubKeyNames().Contains("localapps"))
        {
            using (RegistryKey key = Registry.ClassesRoot.CreateSubKey("localapps"))
            {
                key.SetValue("", "URL:LocalApps Protocol");
                key.SetValue("URL Protocol", "");

                using (RegistryKey shell = key.CreateSubKey("shell"))
                using (RegistryKey open = shell.CreateSubKey("open"))
                using (RegistryKey command = open.CreateSubKey("command"))
                {
                    string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? AppDomain.CurrentDomain.FriendlyName;
                    command.SetValue("", $"\"{exePath}\" --protocol \"%1\"");
                }
            }
            Console.WriteLine("✅ LocalApps protocol registered successfully");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Could not register LocalApps protocol: {ex.Message}");
        Console.WriteLine("Run as administrator once to register");
    }
}

// ========== PROTOCOL HANDLER INVOCATION ==========
var cmdArgs = Environment.GetCommandLineArgs();
if (cmdArgs.Length > 1 && cmdArgs[1] == "--protocol" && cmdArgs.Length > 2)
{
    string protocolUrl = cmdArgs[2];
    Console.WriteLine($"🔌 Handling protocol: {protocolUrl}");
    
    try
    {
        string urlWithoutProtocol = protocolUrl.Replace("localapps://", "");
        string[] parts = urlWithoutProtocol.Split('/');
        string portAndHost = parts[0];
        string path = parts.Length > 1 ? "/" + string.Join("/", parts.Skip(1)) : "";
        
        string portStr = portAndHost.Split(':')[0];
        int port = 5000;
        
        if (int.TryParse(portStr, out int parsedPort))
        {
            port = parsedPort;
            Console.WriteLine($"✅ Using port: {port}");
        }
        
        string localUrl = $"http://localhost:{port}{path}";
        Console.WriteLine($"🌐 Local URL: {localUrl}");
        
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        
        var form = new Form
        {
            Text = $"PackArcade2 - Port {port}",
            WindowState = FormWindowState.Maximized,
            StartPosition = FormStartPosition.CenterScreen
        };
        
        var webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        
        form.Controls.Add(webView);
        
        webView.CoreWebView2InitializationCompleted += (s, e) =>
        {
            if (webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.Settings.IsScriptEnabled = true;
                webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = true;
                
                Console.WriteLine($"🌐 Navigating to: {localUrl}");
                webView.CoreWebView2.Navigate(localUrl);
                
                webView.CoreWebView2.NavigationCompleted += (sender, args) =>
                {
                    if (args.IsSuccess)
                    {
                        Console.WriteLine($"✅ Successfully loaded: {localUrl}");
                        
                        webView.CoreWebView2.DocumentTitleChanged += (titleSender, titleArgs) =>
                        {
                            form.Text = $"PackArcade2 - {webView.CoreWebView2.DocumentTitle}";
                        };
                    }
                    else
                    {
                        Console.WriteLine($"❌ Navigation failed: {args.WebErrorStatus}");
                    }
                };
                
                webView.CoreWebView2.NewWindowRequested += (sender, args) =>
                {
                    args.Handled = true;
                    webView.CoreWebView2.Navigate(args.Uri);
                };
            }
        };
        
        var initializationTask = webView.EnsureCoreWebView2Async();
        
        Application.Run(form);
        
        Console.WriteLine($"✅ Launched native window for port {port}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error handling protocol: {ex.Message}");
        MessageBox.Show($"Error: {ex.Message}");
    }
    
    return;
}

// ========== CONFIGURE PIPELINE ==========
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

var cacheMaxAgeOneWeek = (60 * 60 * 24 * 7).ToString();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append(
            "Cache-Control", $"public, max-age={cacheMaxAgeOneWeek}");
    }
});

app.UseRouting();

// ========== AUTHENTICATION MIDDLEWARE ==========
app.UseAuthentication();
app.UseAuthorization();

// ========== CUSTOM MIDDLEWARE ==========
app.UseMiddleware<ServerStatusMiddleware>();
app.UseMiddleware<PackArcade2.Middleware.FirewallMiddleware>();
app.UseMiddleware<PackArcade2.Middleware.PointsMiddleware>(); // Add PointsMiddleware

// ========== YARP REVERSE PROXY MIDDLEWARE ==========
app.MapReverseProxy();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// ========== ADMIN ROUTES ==========
app.MapGet("/admin/login", async context =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Redirect("/admin");
        return;
    }
    
    var error = context.Request.Query.ContainsKey("error");
    
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync($@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <title>Admin Login - PackArcade2</title>
    <style>
        body {{
            background: linear-gradient(135deg, #0a0e1a 0%, #0f111a 100%);
            font-family: 'Segoe UI', Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
        }}
        .login-container {{
            background: rgba(15, 17, 26, 0.95);
            border-radius: 16px;
            padding: 40px;
            width: 400px;
            box-shadow: 0 25px 50px -12px rgba(0,0,0,0.5);
            border: 1px solid #2a2e3a;
        }}
        h1 {{
            color: #00ffaa;
            margin-bottom: 8px;
            font-size: 28px;
        }}
        .subtitle {{
            color: #6c7293;
            margin-bottom: 32px;
            font-size: 14px;
        }}
        input {{
            width: 100%;
            padding: 12px;
            background: #1a1e2a;
            border: 1px solid #2a2e3a;
            border-radius: 8px;
            color: #00ffaa;
            font-size: 16px;
            margin-bottom: 20px;
            font-family: monospace;
        }}
        input:focus {{
            outline: none;
            border-color: #00ffaa;
        }}
        button {{
            width: 100%;
            padding: 12px;
            background: #00ffaa;
            color: #0a0e1a;
            border: none;
            border-radius: 8px;
            font-weight: bold;
            font-size: 16px;
            cursor: pointer;
        }}
        button:hover {{
            background: #00cc88;
        }}
        .error {{
            color: #ff4444;
            margin-bottom: 16px;
            padding: 8px;
            background: #ff444420;
            border-radius: 8px;
            text-align: center;
        }}
        .info {{
            color: #00ffaa;
            margin-top: 16px;
            padding: 8px;
            background: #00ffaa20;
            border-radius: 8px;
            text-align: center;
            font-size: 12px;
        }}
    </style>
</head>
<body>
    <div class='login-container'>
        <h1>🔒 Admin Login</h1>
        <div class='subtitle'>Enter your administrator password</div>
        {(error ? "<div class='error'>Invalid password. Please try again.</div>" : "")}
        <form method='post' action='/api/admin/login' accept-charset='UTF-8'>
            <input type='password' name='password' placeholder='Password' autofocus />
            <button type='submit'>Login</button>
        </form>
        <div class='info'>
            ⚡ First time? Check the server console or desktop for the password
        </div>
    </div>
</body>
</html>");
});

// ========== ADMIN LOGIN API ==========
app.MapPost("/api/admin/login", async context =>
{
    try
    {
        // Read form data with proper encoding
        string password = "";
        
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            password = form["password"].ToString();
        }
        
        // URL decode in case it was encoded
        password = HttpUtility.UrlDecode(password);
        
        var authService = context.RequestServices.GetRequiredService<AdminAuthenticationService>();
        var success = await authService.SignIn(password);
        
        if (success)
        {
            context.Response.Redirect("/admin/panel");
        }
        else
        {
            context.Response.Redirect("/admin/login?error=1");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Login error");
        context.Response.Redirect("/admin/login?error=1");
    }
});

// ========== ADMIN LOGOUT API ==========
app.MapPost("/api/admin/logout", async context =>
{
    var authService = context.RequestServices.GetRequiredService<AdminAuthenticationService>();
    await authService.SignOut();
    context.Response.Redirect("/");
});

// ========== ADMIN PANEL REDIRECT ==========
app.MapGet("/admin", async context =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/admin/login");
        return;
    }
    
    context.Response.Redirect("/admin/panel");
});

// ========== HOT RELOAD API ENDPOINT ==========
app.MapPost("/api/reload", async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("🔄 Hot reload triggered via API - application will reload on next request");
    context.Response.StatusCode = 200;
    await context.Response.WriteAsync("Reload triggered - file changes detected");
});

// ========== AGENT WEBSOCKET API ENDPOINTS ==========
app.Map("/api/agent/connect", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        
        var serverId = context.Request.Query["serverId"].ToString();
        if (string.IsNullOrEmpty(serverId))
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Server ID required", CancellationToken.None);
            return;
        }
        
        var connectionService = context.RequestServices.GetRequiredService<ServerConnectionService>();
        var success = await connectionService.RegisterAgentConnection(serverId, webSocket);
        
        if (!success)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid server ID", CancellationToken.None);
            return;
        }
        
        // Keep the connection alive
        var buffer = new byte[1024 * 4];
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
            }
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "WebSocket error");
            try { await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Error", CancellationToken.None); } catch { }
        }
    }
    else
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection required");
    }
});

app.MapPost("/api/agent/sync", async context =>
{
    try
    {
        var connectionService = context.RequestServices.GetRequiredService<ServerConnectionService>();
        
        var json = await context.Request.ReadFromJsonAsync<AgentSyncRequest>();
        if (json == null || string.IsNullOrEmpty(json.serverId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request");
            return;
        }
        
        if (!string.IsNullOrEmpty(json.path) && !string.IsNullOrEmpty(json.content))
        {
            var bytes = Convert.FromBase64String(json.content);
            var success = await connectionService.SyncFileToAgent(json.serverId, json.path, bytes);
            
            if (success)
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = true }));
            }
            else
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message = "Agent not connected" }));
            }
        }
        else
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Path and content required");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error syncing to agent");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapPost("/api/agent/delete", async context =>
{
    try
    {
        var connectionService = context.RequestServices.GetRequiredService<ServerConnectionService>();
        
        var json = await context.Request.ReadFromJsonAsync<AgentDeleteRequest>();
        if (json == null || string.IsNullOrEmpty(json.serverId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request");
            return;
        }
        
        var server = connectionService.GetServer(json.serverId);
        if (server != null)
        {
            var fullPath = Path.Combine(server.FolderPath, json.path);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogInformation($"Deleted file via agent: {json.path}");
            }
        }
        
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { success = true }));
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error deleting file from agent");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

// ========== PK_STORAGE API ENDPOINTS ==========
app.MapPost("/api/pk-storage/generate-key", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var apiKey = await storageService.GenerateApiKey(ip);
        
        if (!string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { 
                key = apiKey,
                message = "API key generated successfully! You have 1TB of storage on the VHD.",
                storage_limit = "1 TB"
            }));
        }
        else
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Failed to generate API key");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error generating PK storage key");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapPost("/api/pk-storage/upload", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        // Get API key from header
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var form = await context.Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        
        if (file == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("No file uploaded");
            return;
        }
        
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var data = ms.ToArray();
        
        var success = await storageService.StoreData(apiKey!, file.FileName, data);
        
        if (success)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                message = $"File {file.FileName} uploaded successfully to VHD storage",
                size = data.Length,
                size_mb = data.Length / (1024.0 * 1024.0)
            }));
        }
        else
        {
            context.Response.StatusCode = 507;
            await context.Response.WriteAsync("Storage limit reached or invalid API key");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error uploading to PK storage");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapGet("/api/pk-storage/download/{fileName}", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        // Get API key from header
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var fileName = context.Request.RouteValues["fileName"]?.ToString();
        if (string.IsNullOrEmpty(fileName))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("File name required");
            return;
        }
        
        var data = await storageService.RetrieveData(apiKey!, fileName);
        
        if (data == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("File not found");
            return;
        }
        
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");
        await context.Response.Body.WriteAsync(data);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error downloading from PK storage");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapDelete("/api/pk-storage/delete/{fileName}", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        // Get API key from header
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var fileName = context.Request.RouteValues["fileName"]?.ToString();
        if (string.IsNullOrEmpty(fileName))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("File name required");
            return;
        }
        
        var success = await storageService.DeleteData(apiKey!, fileName);
        
        if (success)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                message = $"File {fileName} deleted from VHD storage"
            }));
        }
        else
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("File not found or invalid API key");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error deleting from PK storage");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapGet("/api/pk-storage/list", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        // Get API key from header
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var files = storageService.ListFiles(apiKey!);
        var keyInfo = storageService.GetApiKeyInfo(apiKey!);
        
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            files = files,
            total_files = files.Count,
            storage_used = keyInfo?.StorageUsed ?? 0,
            storage_used_mb = (keyInfo?.StorageUsed ?? 0) / (1024.0 * 1024.0),
            storage_limit_gb = 1024,
            storage_used_percent = ((keyInfo?.StorageUsed ?? 0) / (1024.0 * 1024 * 1024 * 1024)) * 100
        }));
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error listing PK storage files");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

// ========== PK_STORAGE DATABASE API ENDPOINTS ==========
app.MapPost("/api/pk-storage/database/create", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var json = await context.Request.ReadFromJsonAsync<CreateDatabaseRequest>();
        if (json == null || string.IsNullOrEmpty(json.name))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Database name required");
            return;
        }
        
        var result = await storageService.CreateDatabase(apiKey!, json.name, json.enableRLS, json.isPrivate);
        context.Response.StatusCode = result.Contains("successfully") ? 200 : 400;
        await context.Response.WriteAsync(result);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error creating database");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapPost("/api/pk-storage/database/query", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var json = await context.Request.ReadFromJsonAsync<QueryRequest>();
        if (json == null || string.IsNullOrEmpty(json.database) || string.IsNullOrEmpty(json.sql))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Database name and SQL query required");
            return;
        }
        
        var result = await storageService.ExecuteSql(apiKey!, json.database, json.sql);
        await context.Response.WriteAsync(result);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error executing SQL");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapGet("/api/pk-storage/database/list", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var databases = storageService.ListDatabases(apiKey!);
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { databases }));
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error listing databases");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapGet("/api/pk-storage/database/schema", async context =>
{
    try
    {
        var storageService = context.RequestServices.GetRequiredService<PKStorageService>();
        
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var database = context.Request.Query["database"].ToString();
        if (string.IsNullOrEmpty(database))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Database name required");
            return;
        }
        
        var schema = await storageService.GetDatabaseSchema(apiKey!, database);
        await context.Response.WriteAsync(schema);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error getting database schema");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

// ========== POINTS API ENDPOINTS ==========
app.MapPost("/api/points/extend", async context =>
{
    try
    {
        var pointsService = context.RequestServices.GetRequiredService<PointsService>();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var form = await context.Request.ReadFormAsync();
        var projectName = form["projectName"].ToString();
        var days = int.TryParse(form["days"], out var d) ? d : 1;
        
        if (string.IsNullOrEmpty(projectName))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Project name required");
            return;
        }
        
        var success = await pointsService.ExtendProjectLifespan(ip, projectName, days);
        
        if (success)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"Project '{projectName}' extended by {days} days");
        }
        else
        {
            context.Response.StatusCode = 402;
            await context.Response.WriteAsync("Not enough points");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error extending project");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapPost("/api/points/freeze", async context =>
{
    try
    {
        var pointsService = context.RequestServices.GetRequiredService<PointsService>();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var form = await context.Request.ReadFormAsync();
        var projectName = form["projectName"].ToString();
        var days = int.TryParse(form["days"], out var d) ? d : 1;
        
        if (string.IsNullOrEmpty(projectName))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Project name required");
            return;
        }
        
        var success = await pointsService.FreezeProject(ip, projectName, days);
        
        if (success)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"Project '{projectName}' frozen for {days} days");
        }
        else
        {
            context.Response.StatusCode = 402;
            await context.Response.WriteAsync("Not enough points");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error freezing project");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapPost("/api/points/generate-key", async context =>
{
    try
    {
        var pointsService = context.RequestServices.GetRequiredService<PointsService>();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var apiKey = await pointsService.GenerateApiKey(ip);
        
        if (!string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { key = apiKey }));
        }
        else
        {
            context.Response.StatusCode = 402;
            await context.Response.WriteAsync("Not enough points (500 required)");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error generating API key");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapPost("/api/points/schedule", async context =>
{
    try
    {
        var pointsService = context.RequestServices.GetRequiredService<PointsService>();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var form = await context.Request.ReadFormAsync();
        var projectName = form["projectName"].ToString();
        var runAtStr = form["runAt"].ToString();
        
        if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(runAtStr))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Project name and run time required");
            return;
        }
        
        var runAt = DateTime.Parse(runAtStr);
        var success = await pointsService.ScheduleRun(ip, projectName, runAt);
        
        if (success)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"Run scheduled for {runAt}");
        }
        else
        {
            context.Response.StatusCode = 402;
            await context.Response.WriteAsync("Not enough points (100 required)");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error scheduling run");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

app.MapPost("/api/points/transfer", async context =>
{
    try
    {
        var pointsService = context.RequestServices.GetRequiredService<PointsService>();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var form = await context.Request.ReadFormAsync();
        var toIp = form["toIp"].ToString();
        var amount = int.TryParse(form["amount"], out var a) ? a : 0;
        
        if (string.IsNullOrEmpty(toIp) || amount <= 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Valid recipient and amount required");
            return;
        }
        
        var success = await pointsService.TransferPoints(ip, toIp, amount);
        
        if (success)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"Transferred {amount} points to {toIp}");
        }
        else
        {
            context.Response.StatusCode = 402;
            await context.Response.WriteAsync("Transfer failed - insufficient points");
        }
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error transferring points");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Internal error");
    }
});

// ========== SERVER CONNECTION API ENDPOINTS ==========
app.MapPost("/api/server-connection/deploy", async context =>
{
    try
    {
        var connectionService = context.RequestServices.GetRequiredService<ServerConnectionService>();
        
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var form = await context.Request.ReadFormAsync();
        var serverName = form["serverName"].ToString();
        var configJson = form["config"].ToString();
        var serverFile = form.Files.FirstOrDefault();
        
        if (string.IsNullOrEmpty(serverName) || serverFile == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Server name and files required");
            return;
        }
        
        using var ms = new MemoryStream();
        await serverFile.CopyToAsync(ms);
        var fileData = ms.ToArray();
        
        var result = await connectionService.DeployServer(apiKey!, serverName, fileData, configJson);
        
        context.Response.StatusCode = result.Success ? 200 : 400;
        await context.Response.WriteAsync(JsonSerializer.Serialize(result));
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error deploying server");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapGet("/api/server-connection/list", async context =>
{
    try
    {
        var connectionService = context.RequestServices.GetRequiredService<ServerConnectionService>();
        
        if (!context.Request.Headers.TryGetValue("X-PK-API-Key", out var apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key required in X-PK-API-Key header");
            return;
        }
        
        var servers = connectionService.GetUserServers(apiKey!);
        await context.Response.WriteAsync(JsonSerializer.Serialize(servers));
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error listing servers");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapGet("/api/server-connection/download/{serverId}", async context =>
{
    try
    {
        var connectionService = context.RequestServices.GetRequiredService<ServerConnectionService>();
        
        var serverId = context.Request.RouteValues["serverId"]?.ToString();
        if (string.IsNullOrEmpty(serverId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Server ID required");
            return;
        }
        
        var files = connectionService.GetServerFiles(serverId!);
        if (files == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("Server not found");
            return;
        }
        
        context.Response.ContentType = "application/zip";
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{serverId}.zip\"");
        await context.Response.Body.WriteAsync(files);
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error downloading server files");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

app.MapPost("/api/server-connection/register", async context =>
{
    try
    {
        var json = await context.Request.ReadFromJsonAsync<RegistrationRequest>();
        if (json == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request");
            return;
        }
        
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation($"Server registered: {json.serverId} - {json.serverName}");
        
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync("Registration successful");
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error registering server");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal error: {ex.Message}");
    }
});

// ========== SUBDOMAIN ROUTING ==========
app.MapGet("/", async context =>
{
    var host = context.Request.Host.Host;
    if (host.Contains('.') && !host.StartsWith("www") && host != "packarcade.win")
    {
        var subdomain = host.Split('.')[0];
        
        var backendTracker = context.RequestServices.GetService<BackendTracker>();
        var backend = backendTracker?.GetBackend(subdomain);
        
        if (backend == null)
        {
            context.Response.Redirect($"/play/{subdomain}");
            return;
        }
        
        return;
    }
    
    context.Response.Redirect("/home");
});

// ========== ADD FILE WATCHER CLEANUP ON SHUTDOWN ==========
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    PackArcade2.Middleware.ServerStatusMiddleware.DisposeWatcher();
    Console.WriteLine("🛑 File watcher disposed on shutdown");
});

app.Run();

// ========== GAME SERVICE CLASS ==========
public class GameService
{
    private readonly string gamesPath;
    private readonly IWebHostEnvironment env;

    public GameService(IWebHostEnvironment environment)
    {
        env = environment;
        gamesPath = Path.Combine(env.WebRootPath, "games");
        if (!Directory.Exists(gamesPath))
        {
            Directory.CreateDirectory(gamesPath);
        }
    }

    public async Task<GameInfo?> GetGameInfo(string subdomain)
    {
        var gameDir = Path.Combine(gamesPath, subdomain);
        var infoFile = Path.Combine(gameDir, "info.json");
        
        if (!File.Exists(infoFile))
            return null;
            
        var json = await File.ReadAllTextAsync(infoFile);
        return JsonSerializer.Deserialize<GameInfo>(json);
    }

    public async Task<List<GameInfo>> GetAllGames()
    {
        var games = new List<GameInfo>();
        
        if (!Directory.Exists(gamesPath))
            return games;
            
        foreach (var dir in Directory.GetDirectories(gamesPath))
        {
            var infoFile = Path.Combine(dir, "info.json");
            if (File.Exists(infoFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(infoFile);
                    var game = JsonSerializer.Deserialize<GameInfo>(json);
                    if (game != null)
                        games.Add(game);
                }
                catch { }
            }
        }
        
        return games.OrderByDescending(g => g.Uploaded).ToList();
    }

    public async Task<bool> SaveGame(string subdomain, GameInfo info, byte[] fileData, string fileName)
    {
        try
        {
            var gameDir = Path.Combine(gamesPath, subdomain);
            
            if (!Directory.Exists(gameDir))
                Directory.CreateDirectory(gameDir);
            
            var filePath = Path.Combine(gameDir, fileName);
            await File.WriteAllBytesAsync(filePath, fileData);
            
            info.Subdomain = subdomain;
            info.FileName = fileName;
            info.Uploaded = DateTime.Now;
            
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(gameDir, "info.json"), json);
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetGameFilePath(string subdomain, string fileName)
    {
        return Path.Combine(gamesPath, subdomain, fileName);
    }
}

public class GameInfo
{
    public string Name { get; set; } = "";
    public string Subdomain { get; set; } = "";
    public string GameType { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime Uploaded { get; set; }
    public long FileSize { get; set; }
    public string Description { get; set; } = "";
}

// ========== REQUEST CLASSES ==========
public class CreateDatabaseRequest
{
    public string name { get; set; } = "";
    public bool enableRLS { get; set; } = true;
    public bool isPrivate { get; set; } = true;
}

public class QueryRequest
{
    public string database { get; set; } = "";
    public string sql { get; set; } = "";
}

public class RegistrationRequest
{
    public string serverId { get; set; } = "";
    public string serverName { get; set; } = "";
    public string status { get; set; } = "";
    public string deployedAt { get; set; } = "";
}

// ========== AGENT REQUEST CLASSES ==========
public class AgentSyncRequest
{
    public string serverId { get; set; } = "";
    public string path { get; set; } = "";
    public string content { get; set; } = "";
    public string timestamp { get; set; } = "";
}

public class AgentDeleteRequest
{
    public string serverId { get; set; } = "";
    public string path { get; set; } = "";
}