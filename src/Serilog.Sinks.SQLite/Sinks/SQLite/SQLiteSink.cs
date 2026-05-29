// Copyright 2016 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.Batch;
using Serilog.Sinks.Extensions;

namespace Serilog.Sinks.SQLite
{
    internal class SQLiteSink : BatchProvider, ILogEventSink
    {
        private readonly string _databasePath;
        private readonly IFormatProvider _formatProvider;
        private readonly bool _storeTimestampInUtc;
        private readonly uint _maxDatabaseSize;
        private readonly bool _rollOver;
        private readonly string _tableName;
        private readonly string _quotedTable;
        private readonly TimeSpan? _retentionPeriod;
        private readonly Timer _retentionTimer;
        private readonly string _journalMode;

        private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff";
        private const long BytesPerMb = 1_048_576;
        private const long MaxSupportedPages = 5_242_880;
        private const long MaxSupportedPageSize = 4096;
        private const long MaxSupportedDatabaseSize = MaxSupportedPageSize * MaxSupportedPages / BytesPerMb;

        // Microsoft.Data.Sqlite exposes the raw native error code; SQLITE_FULL = 13.
        private const int SqliteFullErrorCode = 13;

        public SQLiteSink(
            string sqlLiteDbPath,
            string tableName,
            IFormatProvider formatProvider,
            bool storeTimestampInUtc,
            TimeSpan? retentionPeriod,
            TimeSpan? retentionCheckInterval,
            uint batchSize = 100,
            uint maxDatabaseSize = 10,
            bool rollOver = true,
            SqliteJournalMode journalMode = SqliteJournalMode.Wal)
            : base(batchSize: (int)batchSize, maxBufferSize: 100_000)
        {
            if (maxDatabaseSize > MaxSupportedDatabaseSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDatabaseSize),
                    $"Database size greater than {MaxSupportedDatabaseSize} MB is not supported");
            }

            _databasePath = sqlLiteDbPath;
            _tableName = tableName;
            _quotedTable = QuoteIdent(tableName);
            _formatProvider = formatProvider;
            _storeTimestampInUtc = storeTimestampInUtc;
            _maxDatabaseSize = maxDatabaseSize;
            _rollOver = rollOver;
            _journalMode = MapJournalMode(journalMode);

            if (retentionPeriod.HasValue)
            {
                _retentionPeriod = new[] { retentionPeriod, TimeSpan.FromMinutes(30) }.Max();
            }

            InitializeDatabase();

            if (_retentionPeriod.HasValue)
            {
                var checkMinutes = retentionCheckInterval.HasValue
                    ? Math.Max(15, (int)retentionCheckInterval.Value.TotalMinutes)
                    : 15;
                checkMinutes = checkMinutes / 15 * 15;

                _retentionTimer = new Timer(
                    _ => ApplyRetentionPolicy(),
                    null,
                    TimeSpan.Zero,
                    TimeSpan.FromMinutes(checkMinutes));
            }
        }

        #region ILogEvent implementation

        public void Emit(LogEvent logEvent) => PushEvent(logEvent);

        #endregion

        private static string QuoteIdent(string name)
            => "\"" + name.Replace("\"", "\"\"") + "\"";

        private static string MapJournalMode(SqliteJournalMode mode) => mode switch
        {
            SqliteJournalMode.Delete   => "DELETE",
            SqliteJournalMode.Truncate => "TRUNCATE",
            SqliteJournalMode.Persist  => "PERSIST",
            SqliteJournalMode.Memory   => "MEMORY",
            SqliteJournalMode.Wal      => "WAL",
            SqliteJournalMode.Off      => "OFF",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown journal mode")
        };

        private void InitializeDatabase()
        {
            using var conn = GetSqLiteConnection();
            CreateSqlTable(conn);
            if (_retentionPeriod.HasValue)
            {
                CreateTimestampIndex(conn);
            }
        }

        private SqliteConnection GetSqLiteConnection()
        {
            var sqlConString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                Pooling = true
            }.ConnectionString;

            var conn = new SqliteConnection(sqlConString);
            conn.Open();
            ConfigureConnection(conn);
            return conn;
        }

        private void ConfigureConnection(SqliteConnection conn)
        {
            // PRAGMAs that the System.Data.SQLite connection-string builder exposed natively
            // must be issued explicitly here. journal_mode is persisted in the DB file (one-time
            // for WAL); the rest are session-scoped and re-applied on every Open.
            var maxPageCount = _maxDatabaseSize * BytesPerMb / MaxSupportedPageSize;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"PRAGMA journal_mode = {_journalMode};" +
                "PRAGMA synchronous = NORMAL;" +
                "PRAGMA cache_size = 500;" +
                $"PRAGMA max_page_count = {maxPageCount};";
            cmd.ExecuteNonQuery();
        }

        private void CreateSqlTable(SqliteConnection sqlConnection)
        {
            var colDefs = "id INTEGER PRIMARY KEY AUTOINCREMENT,";
            colDefs += "Timestamp TEXT,";
            colDefs += "Level VARCHAR(10),";
            colDefs += "Exception TEXT,";
            colDefs += "RenderedMessage TEXT,";
            colDefs += "Properties TEXT";

            using var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText = $"CREATE TABLE IF NOT EXISTS {_quotedTable} ({colDefs})";
            sqlCommand.ExecuteNonQuery();
        }

        private void CreateTimestampIndex(SqliteConnection sqlConnection)
        {
            var indexName = QuoteIdent($"IX_{_tableName}_Timestamp");
            using var sqlCommand = sqlConnection.CreateCommand();
            sqlCommand.CommandText =
                $"CREATE INDEX IF NOT EXISTS {indexName} ON {_quotedTable}(Timestamp)";
            sqlCommand.ExecuteNonQuery();
        }

        private SqliteCommand CreateSqlInsertCommand(SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {_quotedTable} (Timestamp, Level, Exception, RenderedMessage, Properties)" +
                " VALUES (@timeStamp, @level, @exception, @renderedMessage, @properties)";

            cmd.Parameters.Add("@timeStamp", SqliteType.Text);
            cmd.Parameters.Add("@level", SqliteType.Text);
            cmd.Parameters.Add("@exception", SqliteType.Text);
            cmd.Parameters.Add("@renderedMessage", SqliteType.Text);
            cmd.Parameters.Add("@properties", SqliteType.Text);
            return cmd;
        }

        private void ApplyRetentionPolicy()
        {
            var epoch = DateTimeOffset.Now.Subtract(_retentionPeriod.Value);
            try
            {
                using var sqlConnection = GetSqLiteConnection();
                using var cmd = CreateSqlDeleteCommand(sqlConnection, epoch);
                SelfLog.WriteLine("Deleting log entries older than {0}", epoch);
                var ret = cmd.ExecuteNonQuery();
                SelfLog.WriteLine($"{ret} records deleted");
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine($"Retention policy failed: {ex.Message}");
            }
        }

        private void TruncateAndVacuum(SqliteConnection sqlConnection)
        {
            using (var cmd = sqlConnection.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM {_quotedTable}";
                cmd.ExecuteNonQuery();
            }
            // Pooled idle connections retain file locks that block VACUUM. Drop them first.
            SqliteConnection.ClearPool(sqlConnection);
            using (var cmd = sqlConnection.CreateCommand())
            {
                cmd.CommandText = "VACUUM";
                cmd.ExecuteNonQuery();
            }
        }

        private SqliteCommand CreateSqlDeleteCommand(SqliteConnection sqlConnection, DateTimeOffset epoch)
        {
            var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_quotedTable} WHERE Timestamp < @epoch";
            cmd.Parameters.Add("@epoch", SqliteType.Text).Value =
                (_storeTimestampInUtc ? epoch.ToUniversalTime() : epoch).ToString(TimestampFormat);
            return cmd;
        }

        protected override async Task<bool> WriteLogEventAsync(ICollection<LogEvent> logEventsBatch)
        {
            if (logEventsBatch == null || logEventsBatch.Count == 0)
                return true;

            var rows = PrepareRows(logEventsBatch);

            // BatchProvider.PumpAsync calls this serially, so no additional locking is needed.
            using var sqlConnection = GetSqLiteConnection();
            try
            {
                await WriteToDatabaseAsync(rows, sqlConnection).ConfigureAwait(false);
                return true;
            }
            catch (SqliteException e)
            {
                SelfLog.WriteLine(e.Message);

                if (e.SqliteErrorCode != SqliteFullErrorCode)
                    return false;

                if (_rollOver == false)
                {
                    SelfLog.WriteLine("Discarding log excessive of max database");
                    return true;
                }

                var dbExtension = Path.GetExtension(_databasePath);
                var dbDir = Path.GetDirectoryName(_databasePath) ?? "Logs";
                var rollSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                var newFilePath = Path.Combine(dbDir,
                    $"{Path.GetFileNameWithoutExtension(_databasePath)}-{DateTime.Now:yyyyMMdd_HHmmss.ff}-{rollSuffix}{dbExtension}");

                // VACUUM INTO creates an atomic, consistent copy at the destination path.
                // It includes any WAL contents and does not require copying sidecar files.
                // SQLite parses the path as a string literal, so we double up any embedded quotes.
                using (var cmd = sqlConnection.CreateCommand())
                {
                    var escapedPath = newFilePath.Replace("'", "''");
                    cmd.CommandText = $"VACUUM INTO '{escapedPath}'";
                    cmd.ExecuteNonQuery();
                }

                TruncateAndVacuum(sqlConnection);
                await WriteToDatabaseAsync(rows, sqlConnection).ConfigureAwait(false);

                SelfLog.WriteLine($"Rolling database to {newFilePath}");
                return true;
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
                return false;
            }
        }

        private List<PreparedRow> PrepareRows(ICollection<LogEvent> logEventsBatch)
        {
            var rows = new List<PreparedRow>(logEventsBatch.Count);
            foreach (var logEvent in logEventsBatch)
            {
                var timestamp = (_storeTimestampInUtc
                    ? logEvent.Timestamp.ToUniversalTime()
                    : logEvent.Timestamp).ToString(TimestampFormat);

                var rendered = logEvent.MessageTemplate.Render(logEvent.Properties, _formatProvider);

                var props = logEvent.Properties.Count > 0
                    ? logEvent.Properties.Json()
                    : null;

                rows.Add(new PreparedRow(
                    timestamp,
                    logEvent.Level.ToString(),
                    logEvent.Exception?.ToString(),
                    rendered,
                    props));
            }
            return rows;
        }

        private async Task WriteToDatabaseAsync(List<PreparedRow> rows, SqliteConnection sqlConnection)
        {
            using var tr = sqlConnection.BeginTransaction();
            using var sqlCommand = CreateSqlInsertCommand(sqlConnection);
            sqlCommand.Transaction = tr;

            foreach (var row in rows)
            {
                sqlCommand.Parameters["@timeStamp"].Value = row.Timestamp;
                sqlCommand.Parameters["@level"].Value = row.Level;
                sqlCommand.Parameters["@exception"].Value = (object)row.Exception ?? DBNull.Value;
                sqlCommand.Parameters["@renderedMessage"].Value = (object)row.RenderedMessage ?? DBNull.Value;
                sqlCommand.Parameters["@properties"].Value = (object)row.Properties ?? DBNull.Value;
                await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tr.Commit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _retentionTimer != null)
            {
                using var wh = new ManualResetEvent(false);
                if (_retentionTimer.Dispose(wh))
                {
                    wh.WaitOne(TimeSpan.FromSeconds(30));
                }
            }

            base.Dispose(disposing);
        }

        private readonly struct PreparedRow
        {
            public PreparedRow(string timestamp, string level, string exception, string renderedMessage, string properties)
            {
                Timestamp = timestamp;
                Level = level;
                Exception = exception;
                RenderedMessage = renderedMessage;
                Properties = properties;
            }

            public string Timestamp { get; }
            public string Level { get; }
            public string Exception { get; }
            public string RenderedMessage { get; }
            public string Properties { get; }
        }
    }
}
