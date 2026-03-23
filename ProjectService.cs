using System.Text.Json;

public class ProjectService
{
    private readonly string projectsPath;
    private readonly IWebHostEnvironment env;

    public ProjectService(IWebHostEnvironment environment)
    {
        env = environment;
        projectsPath = Path.Combine(env.WebRootPath, "projects");
        if (!Directory.Exists(projectsPath))
        {
            Directory.CreateDirectory(projectsPath);
        }
    }

    public async Task<ProjectInfo?> GetProjectInfo(string subdomain)
    {
        var projectDir = Path.Combine(projectsPath, subdomain);
        var infoFile = Path.Combine(projectDir, "project.json");
        
        if (!File.Exists(infoFile))
            return null;
            
        var json = await File.ReadAllTextAsync(infoFile);
        return JsonSerializer.Deserialize<ProjectInfo>(json);
    }

    public async Task<List<ProjectInfo>> GetAllProjects()
    {
        var projects = new List<ProjectInfo>();
        
        if (!Directory.Exists(projectsPath))
            return projects;
            
        foreach (var dir in Directory.GetDirectories(projectsPath))
        {
            var infoFile = Path.Combine(dir, "project.json");
            if (File.Exists(infoFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(infoFile);
                    var project = JsonSerializer.Deserialize<ProjectInfo>(json);
                    if (project != null)
                        projects.Add(project);
                }
                catch { }
            }
        }
        
        return projects.OrderByDescending(p => p.LastSaved).ToList();
    }

    public async Task<bool> SaveProject(string subdomain, ProjectInfo info, List<ProjectComponent> components)
    {
        try
        {
            var projectDir = Path.Combine(projectsPath, subdomain);
            
            if (!Directory.Exists(projectDir))
                Directory.CreateDirectory(projectDir);
            
            // Save project info
            info.Subdomain = subdomain;
            info.LastSaved = DateTime.Now;
            
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(projectDir, "project.json"), json);
            
            // Save each component
            foreach (var component in components)
            {
                var componentJson = JsonSerializer.Serialize(component);
                await File.WriteAllTextAsync(Path.Combine(projectDir, $"{component.Id}.json"), componentJson);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving project: {ex.Message}");
            return false;
        }
    }

    public async Task<List<ProjectComponent>> GetProjectComponents(string subdomain)
    {
        var components = new List<ProjectComponent>();
        var projectDir = Path.Combine(projectsPath, subdomain);
        
        if (!Directory.Exists(projectDir))
            return components;
            
        foreach (var file in Directory.GetFiles(projectDir, "*.json"))
        {
            if (Path.GetFileName(file) == "project.json")
                continue;
                
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var component = JsonSerializer.Deserialize<ProjectComponent>(json);
                if (component != null)
                    components.Add(component);
            }
            catch { }
        }
        
        return components;
    }

    public async Task<bool> DeleteProject(string subdomain)
    {
        try
        {
            var projectDir = Path.Combine(projectsPath, subdomain);
            if (Directory.Exists(projectDir))
            {
                Directory.Delete(projectDir, true);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // NEW: Get all files for a project (for editor)
    public async Task<List<ProjectFile>> GetProjectFiles(string subdomain)
    {
        var files = new List<ProjectFile>();
        var projectDir = Path.Combine(projectsPath, subdomain);
        
        if (!Directory.Exists(projectDir))
            return files;
        
        // Get all non-JSON files (actual project files)
        foreach (var file in Directory.GetFiles(projectDir))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".html") || fileName.EndsWith(".css") || fileName.EndsWith(".js") || fileName.EndsWith(".json"))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file);
                    files.Add(new ProjectFile
                    {
                        Name = fileName,
                        Content = content,
                        Type = GetFileType(fileName)
                    });
                }
                catch { }
            }
        }
        
        return files;
    }

    // NEW: Save a file to a project
    public async Task<bool> SaveProjectFile(string subdomain, string fileName, string content)
    {
        try
        {
            var projectDir = Path.Combine(projectsPath, subdomain);
            if (!Directory.Exists(projectDir))
                Directory.CreateDirectory(projectDir);
            
            var filePath = Path.Combine(projectDir, fileName);
            await File.WriteAllTextAsync(filePath, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // NEW: Delete a file from a project
    public async Task<bool> DeleteProjectFile(string subdomain, string fileName)
    {
        try
        {
            var filePath = Path.Combine(projectsPath, subdomain, fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // NEW: Create default project files
    public async Task<bool> CreateDefaultProject(string subdomain, string projectName)
    {
        try
        {
            var projectDir = Path.Combine(projectsPath, subdomain);
            Directory.CreateDirectory(projectDir);

            // Create default HTML file
            var htmlContent = @"<!DOCTYPE html>
<html>
<head>
    <title>" + projectName + @"</title>
    <link rel=""stylesheet"" href=""style.css"">
</head>
<body>
    <h1>Welcome to " + projectName + @"</h1>
    <p>This is a new project created with PackArcade2.</p>
    <script src=""script.js""></script>
</body>
</html>";
            await File.WriteAllTextAsync(Path.Combine(projectDir, "index.html"), htmlContent);

            // Create default CSS file
            var cssContent = @"body {
    font-family: Arial, sans-serif;
    margin: 40px;
    background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    color: white;
    min-height: 100vh;
}

h1 {
    text-align: center;
    margin-bottom: 20px;
}";
            await File.WriteAllTextAsync(Path.Combine(projectDir, "style.css"), cssContent);

            // Create default JS file
            var jsContent = @"// JavaScript for " + projectName + @"
console.log('Project loaded successfully!');

document.addEventListener('DOMContentLoaded', function() {
    console.log('DOM fully loaded');
});";
            await File.WriteAllTextAsync(Path.Combine(projectDir, "script.js"), jsContent);

            // Create project info
            var projectInfo = new ProjectInfo
            {
                Name = projectName,
                Subdomain = subdomain,
                Created = DateTime.Now,
                LastSaved = DateTime.Now,
                Type = "web"
            };

            var json = JsonSerializer.Serialize(projectInfo, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(projectDir, "project.json"), json);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetFileType(string fileName)
    {
        if (fileName.EndsWith(".html")) return "html";
        if (fileName.EndsWith(".css")) return "css";
        if (fileName.EndsWith(".js")) return "js";
        if (fileName.EndsWith(".json")) return "json";
        return "text";
    }
}

public class ProjectInfo
{
    public string Name { get; set; } = "";
    public string Subdomain { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime LastSaved { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Type { get; set; } = "web"; // web, game, app
    public bool IsPublished { get; set; }
}

public class ProjectComponent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Content { get; set; } = "";
    public Dictionary<string, object> Properties { get; set; } = new();
}

public class ProjectFile
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string Type { get; set; } = "";
}