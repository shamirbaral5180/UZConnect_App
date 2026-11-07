using Microsoft.Data.Sqlite;
using UZConnect.Core.Abstractions;
using UZConnect.Core.Models;

namespace UZConnect.Windows.Data;

public sealed class SqliteMessageStore : IMessageStore, IUssdCommandStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteMessageStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? AppPaths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_serial TEXT NOT NULL,
                device_message_id INTEGER NOT NULL,
                folder INTEGER NOT NULL,
                address TEXT NOT NULL,
                body TEXT NOT NULL,
                timestamp_utc INTEGER NOT NULL,
                is_read INTEGER NOT NULL,
                thread_id INTEGER NOT NULL,
                status INTEGER NOT NULL,
                error_code INTEGER NULL,
                service_center TEXT NULL,
                cached_at_utc INTEGER NOT NULL,
                UNIQUE(device_serial, device_message_id, folder)
            );

            CREATE INDEX IF NOT EXISTS ix_messages_folder
                ON messages(device_serial, folder, timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS send_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_serial TEXT NOT NULL,
                to_address TEXT NOT NULL,
                body TEXT NOT NULL,
                parts INTEGER NOT NULL,
                status TEXT NOT NULL,
                error TEXT NULL,
                created_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_send_history_device
                ON send_history(device_serial, created_at_utc DESC);

            CREATE TABLE IF NOT EXISTS ussd_commands (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                code TEXT NOT NULL,
                created_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL
            );

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task<SmsSyncResult> ReplaceFolderAsync(
        string deviceSerial,
        SmsFolder folder,
        IReadOnlyList<SmsMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = new Dictionary<long, (string Body, bool IsRead, long Timestamp)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT device_message_id, body, is_read, timestamp_utc FROM messages WHERE device_serial = $serial AND folder = $folder;";
            select.Parameters.AddWithValue("$serial", deviceSerial);
            select.Parameters.AddWithValue("$folder", (int)folder);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing[reader.GetInt64(0)] = (reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3));
            }
        }

        var added = 0;
        var updated = 0;
        var incomingIds = new HashSet<long>();

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            incomingIds.Add(message.DeviceMessageId);
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO messages(
                    device_serial, device_message_id, folder, address, body, timestamp_utc,
                    is_read, thread_id, status, error_code, service_center, cached_at_utc)
                VALUES($serial, $id, $folder, $address, $body, $timestamp,
                    $read, $thread, $status, $error, $center, $cached)
                ON CONFLICT(device_serial, device_message_id, folder) DO UPDATE SET
                    address = excluded.address,
                    body = excluded.body,
                    timestamp_utc = excluded.timestamp_utc,
                    is_read = excluded.is_read,
                    thread_id = excluded.thread_id,
                    status = excluded.status,
                    error_code = excluded.error_code,
                    service_center = excluded.service_center,
                    cached_at_utc = excluded.cached_at_utc;
                """;
            upsert.Parameters.AddWithValue("$serial", deviceSerial);
            upsert.Parameters.AddWithValue("$id", message.DeviceMessageId);
            upsert.Parameters.AddWithValue("$folder", (int)folder);
            upsert.Parameters.AddWithValue("$address", message.Address ?? string.Empty);
            upsert.Parameters.AddWithValue("$body", message.Body ?? string.Empty);
            upsert.Parameters.AddWithValue("$timestamp", message.Timestamp.ToUnixTimeMilliseconds());
            upsert.Parameters.AddWithValue("$read", message.IsRead ? 1 : 0);
            upsert.Parameters.AddWithValue("$thread", message.ThreadId);
            upsert.Parameters.AddWithValue("$status", message.Status);
            upsert.Parameters.AddWithValue("$error", (object?)message.ErrorCode ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$center", (object?)message.ServiceCenter ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$cached", message.CachedAt.ToUnixTimeMilliseconds());
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (existing.TryGetValue(message.DeviceMessageId, out var old))
            {
                if (old.Body != message.Body || old.IsRead != message.IsRead || old.Timestamp != message.Timestamp.ToUnixTimeMilliseconds())
                {
                    updated++;
                }
            }
            else
            {
                added++;
            }
        }

        var removed = 0;
        if (incomingIds.Count == 0)
        {
            await using var deleteAll = connection.CreateCommand();
            deleteAll.Transaction = transaction;
            deleteAll.CommandText = "DELETE FROM messages WHERE device_serial = $serial AND folder = $folder;";
            deleteAll.Parameters.AddWithValue("$serial", deviceSerial);
            deleteAll.Parameters.AddWithValue("$folder", (int)folder);
            removed = await deleteAll.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            var parameterNames = new List<string>();
            var index = 0;
            foreach (var id in incomingIds)
            {
                var name = "$id" + index++;
                parameterNames.Add(name);
                delete.Parameters.AddWithValue(name, id);
            }

            delete.CommandText =
                $"DELETE FROM messages WHERE device_serial = $serial AND folder = $folder AND device_message_id NOT IN ({string.Join(", ", parameterNames)});";
            delete.Parameters.AddWithValue("$serial", deviceSerial);
            delete.Parameters.AddWithValue("$folder", (int)folder);
            removed = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SmsSyncResult(messages.Count, added, updated, removed, null);
    }

    public async Task<IReadOnlyList<SmsMessage>> GetMessagesAsync(
        string deviceSerial,
        SmsFolder folder,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SmsMessage>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT device_serial, device_message_id, folder, address, body, timestamp_utc,
                   is_read, thread_id, status, error_code, service_center, cached_at_utc
            FROM messages
            WHERE device_serial = $serial AND folder = $folder
            ORDER BY timestamp_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$folder", (int)folder);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SmsMessage(
                reader.GetString(0),
                reader.GetInt64(1),
                (SmsFolder)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.GetInt64(6) != 0,
                reader.GetInt64(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11))));
        }

        return result;
    }

    public async Task<IReadOnlyList<SmsFolderCount>> GetFolderCountsAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SmsFolderCount>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT folder, COUNT(*), SUM(CASE WHEN is_read = 0 THEN 1 ELSE 0 END)
            FROM messages
            WHERE device_serial = $serial
            GROUP BY folder;
            """;
        command.Parameters.AddWithValue("$serial", deviceSerial);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SmsFolderCount(
                (SmsFolder)reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));
        }

        return result;
    }

    public async Task MarkReadAsync(
        string deviceSerial,
        long deviceMessageId,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE messages SET is_read = $read WHERE device_serial = $serial AND device_message_id = $id;";
        command.Parameters.AddWithValue("$read", isRead ? 1 : 0);
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$id", deviceMessageId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string deviceSerial, long deviceMessageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE device_serial = $serial AND device_message_id = $id;";
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$id", deviceMessageId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> AddSendHistoryAsync(
        string deviceSerial,
        string to,
        string body,
        int parts,
        string status,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO send_history(device_serial, to_address, body, parts, status, error, created_at_utc, updated_at_utc)
            VALUES($serial, $to, $body, $parts, $status, $error, $now, $now);
            SELECT last_insert_rowid();
            """;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$parts", parts);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result);
    }

    public async Task UpdateSendHistoryAsync(
        long id,
        string status,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE send_history SET status = $status, error = $error, updated_at_utc = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SmsSendHistoryEntry>> GetSendHistoryAsync(
        string deviceSerial,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SmsSendHistoryEntry>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, device_serial, to_address, body, parts, status, error, created_at_utc, updated_at_utc
            FROM send_history
            WHERE device_serial = $serial
            ORDER BY created_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SmsSendHistoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        }

        return result;
    }

    public async Task DeleteFromFolderAsync(
        string deviceSerial,
        long deviceMessageId,
        SmsFolder folder,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM messages WHERE device_serial = $serial AND device_message_id = $id AND folder = $folder;";
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$id", deviceMessageId);
        command.Parameters.AddWithValue("$folder", (int)folder);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveToRecycleAsync(
        string deviceSerial,
        IReadOnlyList<SmsMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var message in messages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO messages(
                    device_serial, device_message_id, folder, address, body, timestamp_utc,
                    is_read, thread_id, status, error_code, service_center, cached_at_utc)
                VALUES($serial, $id, $folder, $address, $body, $timestamp,
                    $read, $thread, $status, $error, $center, $cached)
                ON CONFLICT(device_serial, device_message_id, folder) DO UPDATE SET
                    address = excluded.address,
                    body = excluded.body,
                    timestamp_utc = excluded.timestamp_utc,
                    is_read = excluded.is_read,
                    cached_at_utc = excluded.cached_at_utc;
                """;
            command.Parameters.AddWithValue("$serial", deviceSerial);
            command.Parameters.AddWithValue("$id", message.DeviceMessageId);
            command.Parameters.AddWithValue("$folder", (int)SmsFolder.Recycle);
            command.Parameters.AddWithValue("$address", message.Address ?? string.Empty);
            command.Parameters.AddWithValue("$body", message.Body ?? string.Empty);
            command.Parameters.AddWithValue("$timestamp", message.Timestamp.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$read", 1);
            command.Parameters.AddWithValue("$thread", message.ThreadId);
            command.Parameters.AddWithValue("$status", message.Status);
            command.Parameters.AddWithValue("$error", (object?)message.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$center", (object?)message.ServiceCenter ?? DBNull.Value);
            command.Parameters.AddWithValue("$cached", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> EmptyRecycleAsync(string deviceSerial, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages WHERE device_serial = $serial AND folder = $folder;";
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$folder", (int)SmsFolder.Recycle);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SmsMessage> SaveDraftAsync(
        string deviceSerial,
        string to,
        string body,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = -Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds());
        var existing = await GetMessagesAsync(deviceSerial, SmsFolder.Drafts, 1000, cancellationToken).ConfigureAwait(false);
        if (existing.FirstOrDefault() is { } first && string.Equals(first.Address, to, StringComparison.Ordinal))
        {
            id = first.DeviceMessageId;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO messages(
                device_serial, device_message_id, folder, address, body, timestamp_utc,
                is_read, thread_id, status, error_code, service_center, cached_at_utc)
            VALUES($serial, $id, $folder, $address, $body, $timestamp, 1, 0, 0, NULL, NULL, $cached)
            ON CONFLICT(device_serial, device_message_id, folder) DO UPDATE SET
                address = excluded.address,
                body = excluded.body,
                timestamp_utc = excluded.timestamp_utc,
                cached_at_utc = excluded.cached_at_utc;
            """;
        var now = DateTimeOffset.Now;
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$folder", (int)SmsFolder.Drafts);
        command.Parameters.AddWithValue("$address", to);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$timestamp", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$cached", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new SmsMessage(deviceSerial, id, SmsFolder.Drafts, to, body, now, true, 0, 0, null, null, now);
    }

    public async Task DeleteDraftAsync(string deviceSerial, long deviceMessageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM messages WHERE device_serial = $serial AND device_message_id = $id AND folder = $folder;";
        command.Parameters.AddWithValue("$serial", deviceSerial);
        command.Parameters.AddWithValue("$id", deviceMessageId);
        command.Parameters.AddWithValue("$folder", (int)SmsFolder.Drafts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UssdCommand>> GetUssdCommandsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<UssdCommand>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, code FROM ussd_commands ORDER BY name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new UssdCommand(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    public async Task<UssdCommand> SaveUssdCommandAsync(
        long id,
        string name,
        string code,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (id > 0)
        {
            command.CommandText =
                "UPDATE ussd_commands SET name = $name, code = $code, updated_at_utc = $now WHERE id = $id; SELECT $id;";
            command.Parameters.AddWithValue("$id", id);
        }
        else
        {
            command.CommandText =
                """
                INSERT INTO ussd_commands(name, code, created_at_utc, updated_at_utc)
                VALUES($name, $code, $now, $now);
                SELECT last_insert_rowid();
                """;
        }

        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$code", code.Trim());
        command.Parameters.AddWithValue("$now", now);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var newId = Convert.ToInt64(result);
        return new UssdCommand(newId, name.Trim(), code.Trim());
    }

    public async Task DeleteUssdCommandAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ussd_commands WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM messages; DELETE FROM send_history;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string> GetDatabasePathAsync() => Task.FromResult(_databasePath);
}


