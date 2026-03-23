using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Linq;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace PackArcade2.Services
{
    public class PKStorageService
    {
        private readonly ILogger<PKStorageService> _logger;
        private readonly ConcurrentDictionary<string, ApiKeyInfo> _apiKeys = new();
        private readonly ConcurrentDictionary<string, StorageEntry> _storage = new();
        private readonly ConcurrentDictionary<string, UserDatabase> _userDatabases = new();
        private readonly string _vhdPath = @"F:\";
        private readonly string _storagePath = @"F:\PK_Storage";
        private readonly string _dataPath = @"D:\PackArcade\Data\PK_Storage";
        private readonly string _dbPath = @"D:\PackArcade\Data\PK_Storage\Databases";
        private static readonly object _vhdLock = new object();
        private static bool _vhdMounted = false;

        public PKStorageService(ILogger<PKStorageService> logger)
        {
            _logger = logger;
            
            _logger.LogInformation($"PKStorageService initializing...");
            _logger.LogInformation($"Storage path: {_storagePath}");
            _logger.LogInformation($"Data path: {_dataPath}");
            _logger.LogInformation($"Database path: {_dbPath}");
            _logger.LogInformation($"VHD path: {_vhdPath}");
            
            try
            {
                if (!Directory.Exists(_storagePath))
                {
                    _logger.LogWarning($"Storage path does not exist, attempting to create: {_storagePath}");
                    Directory.CreateDirectory(_storagePath);
                    _logger.LogInformation($"Storage path created successfully");
                }
                
                if (!Directory.Exists(_dataPath))
                {
                    _logger.LogWarning($"Data path does not exist, attempting to create: {_dataPath}");
                    Directory.CreateDirectory(_dataPath);
                    _logger.LogInformation($"Data path created successfully");
                }
                
                if (!Directory.Exists(_dbPath))
                {
                    _logger.LogWarning($"Database path does not exist, attempting to create: {_dbPath}");
                    Directory.CreateDirectory(_dbPath);
                    _logger.LogInformation($"Database path created successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create directories");
            }
            
            MountVHD();
            LoadApiKeys();
            LoadUserDatabases();
            
            _logger.LogInformation($"PK Storage Service initialized. Storage path: {_storagePath}");
        }

        private void MountVHD()
        {
            lock (_vhdLock)
            {
                if (_vhdMounted) return;
                
                try
                {
                    if (!Directory.Exists(_vhdPath))
                    {
                        _logger.LogWarning($"VHD not mounted at {_vhdPath}. Attempting to mount...");
                        
                        string vhdFile = @"F:\PackArcade_Storage.vhd";
                        string diskpartScript = Path.Combine(Path.GetTempPath(), "mount_vhd.txt");
                        
                        if (!File.Exists(vhdFile))
                        {
                            _logger.LogInformation("Creating new 1TB VHD file...");
                            string createScript = @"create vdisk file=""F:\PackArcade_Storage.vhd"" maximum=1048576 type=expandable
select vdisk file=""F:\PackArcade_Storage.vhd""
attach vdisk
create partition primary
format fs=ntfs quick
assign letter=F
exit";
                            File.WriteAllText(diskpartScript, createScript);
                        }
                        else
                        {
                            string attachScript = @"select vdisk file=""F:\PackArcade_Storage.vhd""
attach vdisk
exit";
                            File.WriteAllText(diskpartScript, attachScript);
                        }
                        
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "diskpart",
                            Arguments = $"/s \"{diskpartScript}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        
                        var process = System.Diagnostics.Process.Start(psi);
                        if (process != null)
                        {
                            process.WaitForExit(30000);
                        }
                        
                        if (Directory.Exists(_vhdPath))
                        {
                            _vhdMounted = true;
                            _logger.LogInformation("VHD mounted successfully!");
                        }
                        else
                        {
                            _logger.LogError("Failed to mount VHD");
                        }
                    }
                    else
                    {
                        _vhdMounted = true;
                        _logger.LogInformation("VHD already mounted at F: drive");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error mounting VHD");
                }
            }
        }

        private void LoadApiKeys()
        {
            try
            {
                var apiKeyFiles = Directory.GetFiles(_dataPath, "apikey_*.json");
                foreach (var file in apiKeyFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var apiKey = JsonSerializer.Deserialize<ApiKeyInfo>(json);
                        if (apiKey != null && apiKey.Key != null)
                        {
                            _apiKeys[apiKey.Key] = apiKey;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to load API key from {file}");
                    }
                }
                _logger.LogInformation($"Loaded {_apiKeys.Count} API keys");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load API keys");
            }
        }

        private void LoadUserDatabases()
        {
            try
            {
                var dbFiles = Directory.GetFiles(_dbPath, "*.db");
                foreach (var dbFile in dbFiles)
                {
                    try
                    {
                        var dbName = Path.GetFileNameWithoutExtension(dbFile);
                        var dbInfo = new UserDatabase
                        {
                            Name = dbName,
                            Path = dbFile,
                            CreatedAt = File.GetCreationTime(dbFile),
                            LastAccessed = File.GetLastAccessTime(dbFile)
                        };
                        _userDatabases[dbName] = dbInfo;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to load database {dbFile}");
                    }
                }
                _logger.LogInformation($"Loaded {_userDatabases.Count} user databases");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user databases");
            }
        }

        public async Task<string> GenerateApiKey(string ip, string? userId = null)
        {
            try
            {
                _logger.LogInformation($"Starting API key generation for IP: {ip}");
                
                var key = Guid.NewGuid().ToString("N");
                
                _logger.LogInformation($"Generated key: {key}");
                
                var apiKeyInfo = new ApiKeyInfo
                {
                    Key = key,
                    OwnerIP = ip,
                    OwnerId = userId,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddYears(1),
                    StorageUsed = 0,
                    StorageLimit = 1024L * 1024 * 1024 * 1024,
                    RequestsToday = 0,
                    LastReset = DateTime.UtcNow
                };
                
                _apiKeys[key] = apiKeyInfo;
                
                string userStoragePath = Path.Combine(_storagePath, key);
                _logger.LogInformation($"Creating user storage path: {userStoragePath}");
                Directory.CreateDirectory(userStoragePath);
                
                var json = JsonSerializer.Serialize(apiKeyInfo, new JsonSerializerOptions { WriteIndented = true });
                string filePath = Path.Combine(_dataPath, $"apikey_{key}.json");
                _logger.LogInformation($"Saving API key to: {filePath}");
                await File.WriteAllTextAsync(filePath, json);
                
                _logger.LogInformation($"✅ Generated API key for {ip}: {key}");
                return key;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error generating API key for {ip}");
                _logger.LogError($"Exception details: {ex.GetType().Name} - {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return "";
            }
        }

        public bool ValidateApiKey(string apiKey, out ApiKeyInfo? keyInfo)
        {
            keyInfo = null;
            
            if (string.IsNullOrEmpty(apiKey))
                return false;
                
            if (!_apiKeys.TryGetValue(apiKey, out var info))
                return false;
            
            if (info.ExpiresAt < DateTime.UtcNow)
            {
                _apiKeys.TryRemove(apiKey, out _);
                return false;
            }
            
            if (info.LastReset.Date < DateTime.UtcNow.Date)
            {
                info.RequestsToday = 0;
                info.LastReset = DateTime.UtcNow;
            }
            
            keyInfo = info;
            return true;
        }

        // ========== SQL DATABASE OPERATIONS ==========
        
        public async Task<string> CreateDatabase(string apiKey, string dbName, bool enableRLS = true, bool isPrivate = true)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return "Invalid API key";
            
            if (keyInfo == null)
                return "Invalid API key";
            
            string dbPath = Path.Combine(_dbPath, $"{keyInfo.Key}_{dbName}.db");
            
            if (File.Exists(dbPath))
                return "Database already exists";
            
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                
                string createTableSql = @"
                    CREATE TABLE IF NOT EXISTS _metadata (
                        key TEXT PRIMARY KEY,
                        value TEXT
                    );
                    
                    CREATE TABLE IF NOT EXISTS _tables (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT UNIQUE,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                    
                    CREATE TABLE IF NOT EXISTS _row_level_security (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        table_name TEXT,
                        row_id TEXT,
                        user_id TEXT,
                        permission TEXT
                    );
                ";
                
                using var command = new SqliteCommand(createTableSql, connection);
                await command.ExecuteNonQueryAsync();
                
                // Store metadata
                using var metaCommand = new SqliteCommand("INSERT INTO _metadata (key, value) VALUES (@key, @value)", connection);
                metaCommand.Parameters.AddWithValue("@key", "rls_enabled");
                metaCommand.Parameters.AddWithValue("@value", enableRLS ? "true" : "false");
                await metaCommand.ExecuteNonQueryAsync();
                
                metaCommand.Parameters.Clear();
                metaCommand.CommandText = "INSERT INTO _metadata (key, value) VALUES (@key, @value)";
                metaCommand.Parameters.AddWithValue("@key", "is_private");
                metaCommand.Parameters.AddWithValue("@value", isPrivate ? "true" : "false");
                await metaCommand.ExecuteNonQueryAsync();
                
                metaCommand.Parameters.Clear();
                metaCommand.CommandText = "INSERT INTO _metadata (key, value) VALUES (@key, @value)";
                metaCommand.Parameters.AddWithValue("@key", "owner_id");
                metaCommand.Parameters.AddWithValue("@value", keyInfo.Key);
                await metaCommand.ExecuteNonQueryAsync();
                
                var dbInfo = new UserDatabase
                {
                    Name = dbName,
                    Path = dbPath,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    OwnerKey = keyInfo.Key,
                    EnableRLS = enableRLS,
                    IsPrivate = isPrivate
                };
                
                _userDatabases[dbName] = dbInfo;
                
                _logger.LogInformation($"Database created: {dbName} for API key {keyInfo.Key}");
                return "Database created successfully";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create database {dbName}");
                return $"Error: {ex.Message}";
            }
        }
        
        public async Task<string> ExecuteSql(string apiKey, string dbName, string sql)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return "Invalid API key";
            
            if (keyInfo == null)
                return "Invalid API key";
            
            string dbPath = Path.Combine(_dbPath, $"{keyInfo.Key}_{dbName}.db");
            
            if (!File.Exists(dbPath))
                return "Database not found";
            
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                
                // Check RLS permissions if enabled
                bool rlsEnabled = false;
                using var metaCommand = new SqliteCommand("SELECT value FROM _metadata WHERE key = 'rls_enabled'", connection);
                var result = await metaCommand.ExecuteScalarAsync();
                if (result != null && result.ToString() == "true")
                {
                    rlsEnabled = true;
                }
                
                // Parse SQL command (simple parsing for PK_INSERT, PK_SELECT, etc.)
                sql = sql.Trim();
                var results = new List<string>();
                
                if (sql.StartsWith("PK_INSERT:", StringComparison.OrdinalIgnoreCase))
                {
                    var insertSql = sql.Substring(10).Trim();
                    
                    if (rlsEnabled)
                    {
                        // Add RLS check - only allow inserts with proper permissions
                        insertSql = insertSql.Replace("VALUES", $"VALUES ('{keyInfo.Key}', ");
                    }
                    
                    using var insertCommand = new SqliteCommand(insertSql, connection);
                    int rows = await insertCommand.ExecuteNonQueryAsync();
                    results.Add($"Inserted {rows} row(s)");
                }
                else if (sql.StartsWith("PK_SELECT:", StringComparison.OrdinalIgnoreCase))
                {
                    var selectSql = sql.Substring(10).Trim();
                    
                    if (rlsEnabled)
                    {
                        // Add RLS filter for selects
                        if (!selectSql.ToLower().Contains("where"))
                        {
                            selectSql += $" WHERE user_id = '{keyInfo.Key}'";
                        }
                        else
                        {
                            selectSql += $" AND user_id = '{keyInfo.Key}'";
                        }
                    }
                    
                    using var selectCommand = new SqliteCommand(selectSql, connection);
                    using var reader = await selectCommand.ExecuteReaderAsync();
                    
                    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                    results.Add(string.Join("\t", columns));
                    
                    while (await reader.ReadAsync())
                    {
                        var row = new List<string>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row.Add(reader[i]?.ToString() ?? "NULL");
                        }
                        results.Add(string.Join("\t", row));
                    }
                }
                else if (sql.StartsWith("PK_UPDATE:", StringComparison.OrdinalIgnoreCase))
                {
                    var updateSql = sql.Substring(9).Trim();
                    
                    if (rlsEnabled)
                    {
                        if (!updateSql.ToLower().Contains("where"))
                        {
                            updateSql += $" WHERE user_id = '{keyInfo.Key}'";
                        }
                        else
                        {
                            updateSql += $" AND user_id = '{keyInfo.Key}'";
                        }
                    }
                    
                    using var updateCommand = new SqliteCommand(updateSql, connection);
                    int rows = await updateCommand.ExecuteNonQueryAsync();
                    results.Add($"Updated {rows} row(s)");
                }
                else if (sql.StartsWith("PK_DELETE:", StringComparison.OrdinalIgnoreCase))
                {
                    var deleteSql = sql.Substring(9).Trim();
                    
                    if (rlsEnabled)
                    {
                        if (!deleteSql.ToLower().Contains("where"))
                        {
                            deleteSql += $" WHERE user_id = '{keyInfo.Key}'";
                        }
                        else
                        {
                            deleteSql += $" AND user_id = '{keyInfo.Key}'";
                        }
                    }
                    
                    using var deleteCommand = new SqliteCommand(deleteSql, connection);
                    int rows = await deleteCommand.ExecuteNonQueryAsync();
                    results.Add($"Deleted {rows} row(s)");
                }
                else
                {
                    // Regular SQL
                    using var command = new SqliteCommand(sql, connection);
                    if (sql.ToLower().StartsWith("select"))
                    {
                        using var reader = await command.ExecuteReaderAsync();
                        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                        results.Add(string.Join("\t", columns));
                        
                        while (await reader.ReadAsync())
                        {
                            var row = new List<string>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                row.Add(reader[i]?.ToString() ?? "NULL");
                            }
                            results.Add(string.Join("\t", row));
                        }
                    }
                    else
                    {
                        int rows = await command.ExecuteNonQueryAsync();
                        results.Add($"{rows} row(s) affected");
                    }
                }
                
                _userDatabases[dbName].LastAccessed = DateTime.UtcNow;
                return string.Join("\n", results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to execute SQL on {dbName}");
                return $"Error: {ex.Message}";
            }
        }
        
        public async Task<string> GetDatabaseSchema(string apiKey, string dbName)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return "Invalid API key";
            
            string dbPath = Path.Combine(_dbPath, $"{keyInfo.Key}_{dbName}.db");
            
            if (!File.Exists(dbPath))
                return "Database not found";
            
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                
                var schema = new List<string>();
                schema.Add($"Database: {dbName}");
                schema.Add($"Owner: {keyInfo.Key}");
                schema.Add($"Created: {_userDatabases[dbName]?.CreatedAt ?? DateTime.MinValue}");
                schema.Add("");
                schema.Add("Tables:");
                
                using var command = new SqliteCommand("SELECT name FROM _tables", connection);
                using var reader = await command.ExecuteReaderAsync();
                
                while (await reader.ReadAsync())
                {
                    schema.Add($"  - {reader[0]}");
                }
                
                return string.Join("\n", schema);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
        
        public List<string> ListDatabases(string apiKey)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return new List<string>();
            
            return _userDatabases.Values
                .Where(db => db.OwnerKey == keyInfo.Key)
                .Select(db => db.Name)
                .ToList();
        }
        
        // ========== FILE STORAGE OPERATIONS ==========
        
        public async Task<bool> StoreData(string apiKey, string fileName, byte[] data)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return false;
            
            if (keyInfo == null)
                return false;
                
            string userPath = Path.Combine(_storagePath, apiKey);
            Directory.CreateDirectory(userPath);
            
            string filePath = Path.Combine(userPath, fileName);
            
            if (keyInfo.StorageUsed + data.Length > keyInfo.StorageLimit)
                return false;
            
            await File.WriteAllBytesAsync(filePath, data);
            
            keyInfo.StorageUsed += data.Length;
            keyInfo.RequestsToday++;
            
            var json = JsonSerializer.Serialize(keyInfo, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(_dataPath, $"apikey_{apiKey}.json"), json);
            
            _logger.LogInformation($"Stored {data.Length} bytes for API key {apiKey}. Total: {keyInfo.StorageUsed}");
            return true;
        }

        public async Task<byte[]?> RetrieveData(string apiKey, string fileName)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return null;
            
            if (keyInfo == null)
                return null;
                
            string filePath = Path.Combine(_storagePath, apiKey, fileName);
            
            if (!File.Exists(filePath))
                return null;
            
            keyInfo.RequestsToday++;
            var data = await File.ReadAllBytesAsync(filePath);
            
            return data;
        }

        public async Task<bool> DeleteData(string apiKey, string fileName)
        {
            if (!ValidateApiKey(apiKey, out var keyInfo))
                return false;
            
            if (keyInfo == null)
                return false;
                
            string filePath = Path.Combine(_storagePath, apiKey, fileName);
            
            if (!File.Exists(filePath))
                return false;
            
            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;
            
            File.Delete(filePath);
            keyInfo.StorageUsed -= fileSize;
            keyInfo.RequestsToday++;
            
            var json = JsonSerializer.Serialize(keyInfo, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(_dataPath, $"apikey_{apiKey}.json"), json);
            
            _logger.LogInformation($"Deleted {fileName} for API key {apiKey}");
            return true;
        }

        public List<string> ListFiles(string apiKey)
        {
            if (!ValidateApiKey(apiKey, out _))
                return new List<string>();
            
            string userPath = Path.Combine(_storagePath, apiKey);
            
            if (!Directory.Exists(userPath))
                return new List<string>();
            
            var files = Directory.GetFiles(userPath)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .Select(f => f!)
                .ToList();
            
            return files;
        }

        public ApiKeyInfo? GetApiKeyInfo(string apiKey)
        {
            _apiKeys.TryGetValue(apiKey, out var info);
            return info;
        }
    }

    public class ApiKeyInfo
    {
        public string Key { get; set; } = "";
        public string OwnerIP { get; set; } = "";
        public string? OwnerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public long StorageUsed { get; set; }
        public long StorageLimit { get; set; }
        public int RequestsToday { get; set; }
        public DateTime LastReset { get; set; }
    }

    public class StorageEntry
    {
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public DateTime UploadedAt { get; set; }
        public string ContentType { get; set; } = "";
    }
    
    public class UserDatabase
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessed { get; set; }
        public string OwnerKey { get; set; } = "";
        public bool EnableRLS { get; set; } = true;
        public bool IsPrivate { get; set; } = true;
    }
}