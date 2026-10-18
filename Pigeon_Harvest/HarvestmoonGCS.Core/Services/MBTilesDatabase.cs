using System.Threading;
using Microsoft.Data.Sqlite;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// SQLite database implementation for MBTiles storage
/// Stores map tiles with optimized indexing for fast retrieval
/// </summary>
public class MBTilesDatabase : IDisposable
{
    private SqliteConnection? _connection;
    private readonly string _dbPath;
    private readonly ILoggingService? _logger;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new MBTiles database instance
    /// </summary>
    public MBTilesDatabase(ILoggingService? logger = null)
    {
        _logger = logger;
        _dbPath = GetDatabasePath();
        
        EnsureDirectoryExists();
        InitializeDatabase();
    }

    /// <summary>
    /// Gets the platform-specific database file path
    /// </summary>
    private string GetDatabasePath()
    {
        string basePath;
        
#if __ANDROID__
        // Android: /data/data/[package]/files/offline_maps/offline_maps.mbtiles
        var context = Android.App.Application.Context;
        basePath = Path.Combine(context.FilesDir!.AbsolutePath, "offline_maps");
#else
        // Desktop: %LocalAppData%/HarvestmoonGCS/offline_maps/offline_maps.mbtiles
        basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarvestmoonGCS", "offline_maps");
#endif
        
        Directory.CreateDirectory(basePath);
        return Path.Combine(basePath, "offline_maps.mbtiles");
    }

    private void EnsureDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger?.LogInfo($"Created offline maps directory: {directory}", nameof(MBTilesDatabase));
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // Enable WAL mode for better concurrent access
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA cache_size=10000;
                PRAGMA temp_store=MEMORY;
            ";
            cmd.ExecuteNonQuery();

            CreateSchema();
            
            _logger?.LogInfo($"MBTiles database initialized: {_dbPath}", nameof(MBTilesDatabase));
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Failed to initialize MBTiles database: {ex.Message}", nameof(MBTilesDatabase));
            throw;
        }
    }

    private void CreateSchema()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            -- Metadata table (MBTiles spec)
            CREATE TABLE IF NOT EXISTS metadata (
                name TEXT PRIMARY KEY,
                value TEXT
            );

            -- Tiles table (flat schema - most efficient for raster tiles)
            CREATE TABLE IF NOT EXISTS tiles (
                zoom_level INTEGER NOT NULL,
                tile_column INTEGER NOT NULL,
                tile_row INTEGER NOT NULL,
                tile_data BLOB NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (zoom_level, tile_column, tile_row)
            ) WITHOUT ROWID;

            -- Index for fast lookups
            CREATE INDEX IF NOT EXISTS idx_tiles_lookup 
            ON tiles (zoom_level, tile_column, tile_row);
        ";
        cmd.ExecuteNonQuery();

        // Insert default metadata if not exists
        SetMetadata("name", "HarvestmoonGCS Offline Maps");
        SetMetadata("type", "baselayer");
        SetMetadata("version", "1.0");
        SetMetadata("description", "Offline map tiles for Jawa and Kalimantan");
        SetMetadata("format", "png");
        SetMetadata("minzoom", "12");
        SetMetadata("maxzoom", "15");
        SetMetadata("bounds", "105.0,-8.5,119.0,2.0"); // Jawa + Kalimantan combined
    }

    /// <summary>
    /// Sets a metadata value
    /// </summary>
    public void SetMetadata(string name, string value)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO metadata (name, value) 
            VALUES (@name, @value);
        ";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets a metadata value
    /// </summary>
    public string? GetMetadata(string name)
    {
        if (_connection == null) return null;

        _dbLock.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM metadata WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", name);
            
            var result = cmd.ExecuteScalar();
            return result?.ToString();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Inserts multiple tiles in a single transaction (batch insert)
    /// </summary>
    public async Task InsertTilesBatchAsync(IEnumerable<TileData> tiles, int batchSize = 100)
    {
        if (_connection == null) throw new InvalidOperationException("Database not initialized");

        await _dbLock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            
            try
            {
                var batch = new List<TileData>();
                int totalInserted = 0;

                foreach (var tile in tiles)
                {
                    batch.Add(tile);

                    if (batch.Count >= batchSize)
                    {
                        await InsertBatchInternalAsync(batch, transaction);
                        totalInserted += batch.Count;
                        batch.Clear();
                    }
                }

                // Insert remaining tiles
                if (batch.Count > 0)
                {
                    await InsertBatchInternalAsync(batch, transaction);
                    totalInserted += batch.Count;
                }

                transaction.Commit();
                _logger?.LogInfo($"Inserted {totalInserted} tiles", nameof(MBTilesDatabase));
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger?.LogError($"Failed to insert tiles: {ex.Message}", nameof(MBTilesDatabase));
                throw;
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task InsertBatchInternalAsync(List<TileData> tiles, SqliteTransaction transaction)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.Transaction = transaction;
        
        // Use TMS scheme for tile_row (flip Y coordinate)
        cmd.CommandText = @"
            INSERT OR REPLACE INTO tiles (zoom_level, tile_column, tile_row, tile_data)
            VALUES (@zoom, @col, @row, @data);
        ";

        var zoomParam = cmd.Parameters.Add("@zoom", SqliteType.Integer);
        var colParam = cmd.Parameters.Add("@col", SqliteType.Integer);
        var rowParam = cmd.Parameters.Add("@row", SqliteType.Integer);
        var dataParam = cmd.Parameters.Add("@data", SqliteType.Blob);

        foreach (var tile in tiles)
        {
            zoomParam.Value = tile.Z;
            colParam.Value = tile.X;
            rowParam.Value = tile.ToTmsY(); // Convert to TMS Y
            dataParam.Value = tile.ImageData;
            
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Retrieves a single tile from the database
    /// </summary>
    public async Task<byte[]?> GetTileAsync(int z, int x, int y)
    {
        if (_connection == null) return null;

        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT tile_data FROM tiles 
                WHERE zoom_level = @zoom 
                AND tile_column = @col 
                AND tile_row = @row;
            ";
            
            cmd.Parameters.AddWithValue("@zoom", z);
            cmd.Parameters.AddWithValue("@col", x);
            cmd.Parameters.AddWithValue("@row", TileData.FromTmsY(y, z)); // Convert Y to TMS

            var result = await cmd.ExecuteScalarAsync();
            return result as byte[];
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Failed to get tile {z}/{x}/{y}: {ex.Message}", nameof(MBTilesDatabase));
            return null;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Checks if a tile exists in the database
    /// </summary>
    public async Task<bool> HasTileAsync(int z, int x, int y)
    {
        if (_connection == null) return false;

        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT 1 FROM tiles 
                WHERE zoom_level = @zoom 
                AND tile_column = @col 
                AND tile_row = @row
                LIMIT 1;
            ";
            
            cmd.Parameters.AddWithValue("@zoom", z);
            cmd.Parameters.AddWithValue("@col", x);
            cmd.Parameters.AddWithValue("@row", TileData.FromTmsY(y, z));

            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Gets the total number of tiles in the database
    /// </summary>
    public async Task<int> GetTileCountAsync()
    {
        if (_connection == null) return 0;

        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tiles;";
            
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Gets the total storage size in bytes
    /// </summary>
    public long GetStorageSize()
    {
        try
        {
            var fileInfo = new FileInfo(_dbPath);
            return fileInfo.Exists ? fileInfo.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets formatted storage size string (e.g., "512 MB")
    /// </summary>
    public string GetStorageSizeFormatted()
    {
        var bytes = GetStorageSize();
        return FormatBytes(bytes);
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Clears all tiles from the database
    /// </summary>
    public async Task<int> ClearAllTilesAsync()
    {
        if (_connection == null) return 0;

        await _dbLock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tiles;";
            
            var rowsAffected = await cmd.ExecuteNonQueryAsync();
            _logger?.LogInfo($"Cleared {rowsAffected} tiles from database", nameof(MBTilesDatabase));
            
            return rowsAffected;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Clears tiles for a specific region
    /// </summary>
    public async Task<int> ClearRegionAsync(string regionName)
    {
        // Note: This would require storing region info with each tile
        // For now, clear all tiles
        return await ClearAllTilesAsync();
    }

    /// <summary>
    /// Gets the database file path
    /// </summary>
    public string DatabasePath => _dbPath;

    public void Dispose()
    {
        if (!_disposed)
        {
            _dbLock.Wait();
            try
            {
                _connection?.Close();
                _connection?.Dispose();
                _disposed = true;
            }
            finally
            {
                _dbLock.Release();
                _dbLock.Dispose();
            }
        }
    }
}
