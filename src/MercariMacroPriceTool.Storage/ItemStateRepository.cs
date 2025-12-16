using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MercariMacroPriceTool.Domain;

namespace MercariMacroPriceTool.Storage;

/// <summary>
/// Items テーブルへの CRUD 相当処理。
/// </summary>
public class ItemStateRepository
{
    private readonly string _connectionString;
    private bool _initialized;
    private bool _schemaEnsured;

    public ItemStateRepository()
    {
        var dbPath = StoragePathProvider.GetDatabasePath();
        _connectionString = $"Data Source={dbPath}";
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS Items (
    ItemId TEXT PRIMARY KEY,
    ItemUrl TEXT NOT NULL,
    Title TEXT,
    BasePrice INTEGER NOT NULL,
    RunCount INTEGER NOT NULL,
    LastRunDate TEXT,
    UpdatedAt TEXT,
    LastDownAmount INTEGER NOT NULL DEFAULT 0,
    LastDownAt TEXT,
    LastDownRatePercent INTEGER,
    LastDownDailyDownYen INTEGER,
    LastDownRunIndex INTEGER
);";
        await command.ExecuteNonQueryAsync();
        _initialized = true;
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaEnsured)
        {
            return;
        }

        await EnsureInitializedAsync();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(Items);";
            await using var reader = await infoCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        var needsDownAmount = !existingColumns.Contains("LastDownAmount");
        var needsDownAt = !existingColumns.Contains("LastDownAt");
        var needsDownRate = !existingColumns.Contains("LastDownRatePercent");
        var needsDownDaily = !existingColumns.Contains("LastDownDailyDownYen");
        var needsDownIndex = !existingColumns.Contains("LastDownRunIndex");

        try
        {
            if (needsDownAmount)
            {
                await using var alter1 = connection.CreateCommand();
                alter1.CommandText = "ALTER TABLE Items ADD COLUMN LastDownAmount INTEGER NOT NULL DEFAULT 0;";
                await alter1.ExecuteNonQueryAsync();
            }

            if (needsDownAt)
            {
                await using var alter2 = connection.CreateCommand();
                alter2.CommandText = "ALTER TABLE Items ADD COLUMN LastDownAt TEXT;";
                await alter2.ExecuteNonQueryAsync();
            }

            if (needsDownRate)
            {
                await using var alter3 = connection.CreateCommand();
                alter3.CommandText = "ALTER TABLE Items ADD COLUMN LastDownRatePercent INTEGER;";
                await alter3.ExecuteNonQueryAsync();
            }

            if (needsDownDaily)
            {
                await using var alter4 = connection.CreateCommand();
                alter4.CommandText = "ALTER TABLE Items ADD COLUMN LastDownDailyDownYen INTEGER;";
                await alter4.ExecuteNonQueryAsync();
            }

            if (needsDownIndex)
            {
                await using var alter5 = connection.CreateCommand();
                alter5.CommandText = "ALTER TABLE Items ADD COLUMN LastDownRunIndex INTEGER;";
                await alter5.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ItemStateRepository] Schema ensure warning: {ex.Message}");
        }

        _schemaEnsured = true;
    }

    public async Task<ItemState?> GetByItemIdAsync(string itemId)
    {
        await EnsureSchemaAsync();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT ItemId, ItemUrl, Title, BasePrice, RunCount, LastRunDate, UpdatedAt, LastDownAmount, LastDownAt, LastDownRatePercent, LastDownDailyDownYen, LastDownRunIndex
FROM Items
WHERE ItemId = $itemId;";
        command.Parameters.AddWithValue("$itemId", itemId);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (await reader.ReadAsync())
        {
            return new ItemState
            {
                ItemId = reader.GetString(0),
                ItemUrl = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                BasePrice = reader.GetInt32(3),
                RunCount = reader.GetInt32(4),
                LastRunDate = reader.IsDBNull(5) ? null : reader.GetString(5),
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                LastDownAmount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                LastDownAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastDownRatePercent = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetInt32(9) : (int?)null,
                LastDownDailyDownYen = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt32(10) : (int?)null,
                LastDownRunIndex = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetInt32(11) : (int?)null
            };
        }

        return null;
    }

    public async Task UpsertAsync(ItemState item)
    {
        await EnsureSchemaAsync();
        var updatedAt = DateTime.UtcNow.ToString("o");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO Items (ItemId, ItemUrl, Title, BasePrice, RunCount, LastRunDate, UpdatedAt, LastDownAmount, LastDownAt, LastDownRatePercent, LastDownDailyDownYen, LastDownRunIndex)
VALUES ($itemId, $itemUrl, $title, $basePrice, $runCount, $lastRunDate, $updatedAt, $lastDownAmount, $lastDownAt, $lastDownRatePercent, $lastDownDailyDownYen, $lastDownRunIndex)
ON CONFLICT(ItemId) DO UPDATE SET
    ItemUrl = excluded.ItemUrl,
    Title = excluded.Title,
    BasePrice = excluded.BasePrice,
    RunCount = excluded.RunCount,
    LastRunDate = excluded.LastRunDate,
    UpdatedAt = excluded.UpdatedAt,
    LastDownAmount = excluded.LastDownAmount,
    LastDownAt = excluded.LastDownAt,
    LastDownRatePercent = excluded.LastDownRatePercent,
    LastDownDailyDownYen = excluded.LastDownDailyDownYen,
    LastDownRunIndex = excluded.LastDownRunIndex;";

        command.Parameters.AddWithValue("$itemId", item.ItemId);
        command.Parameters.AddWithValue("$itemUrl", item.ItemUrl);
        command.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$basePrice", item.BasePrice);
        command.Parameters.AddWithValue("$runCount", item.RunCount);
        command.Parameters.AddWithValue("$lastRunDate", (object?)item.LastRunDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        command.Parameters.AddWithValue("$lastDownAmount", item.LastDownAmount);
        command.Parameters.AddWithValue("$lastDownAt", (object?)item.LastDownAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastDownRatePercent", (object?)item.LastDownRatePercent ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastDownDailyDownYen", (object?)item.LastDownDailyDownYen ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastDownRunIndex", (object?)item.LastDownRunIndex ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 今日が既存 LastRunDate と同じなら RunCount を増やさない。更新した場合 true。
    /// </summary>
    public async Task<bool> UpdateRunCountIfNewDayAsync(string itemId, string today)
    {
        await EnsureSchemaAsync();

        var existing = await GetByItemIdAsync(itemId);
        if (existing == null)
        {
            return false;
        }

        if (string.Equals(existing.LastRunDate, today, StringComparison.Ordinal))
        {
            return false;
        }

        var newRunCount = existing.RunCount + 1;
        var updatedAt = DateTime.UtcNow.ToString("o");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE Items
SET RunCount = $runCount,
    LastRunDate = $lastRunDate,
    UpdatedAt = $updatedAt
WHERE ItemId = $itemId;";

        command.Parameters.AddWithValue("$runCount", newRunCount);
        command.Parameters.AddWithValue("$lastRunDate", today);
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        command.Parameters.AddWithValue("$itemId", itemId);

        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<bool> ResetItemAsync(string itemId, int resetRunCount, CancellationToken token = default)
    {
        await EnsureSchemaAsync();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE Items
SET RunCount = $runCount,
    LastRunDate = NULL,
    UpdatedAt = $updatedAt,
    LastDownAmount = 0,
    LastDownAt = NULL,
    LastDownRatePercent = NULL,
    LastDownDailyDownYen = NULL,
    LastDownRunIndex = NULL
WHERE ItemId = $itemId;";

        command.Parameters.AddWithValue("$runCount", resetRunCount);
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$itemId", itemId);

        var rows = await command.ExecuteNonQueryAsync(token);
        return rows > 0;
    }

    public async Task<bool> ClearLastRunDateAsync(string itemId, CancellationToken token = default)
    {
        await EnsureSchemaAsync();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE Items
SET LastRunDate = NULL
WHERE ItemId = $itemId;";

        command.Parameters.AddWithValue("$itemId", itemId);

        var rows = await command.ExecuteNonQueryAsync(token);
        return rows > 0;
    }
}
