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

using Serilog.Debugging;

namespace Serilog
{
    using System;
    using System.IO;
    using Serilog.Configuration;
    using Serilog.Core;
    using Serilog.Events;
    using Serilog.Sinks.SQLite;

    /// <summary>
    ///     Adds the WriteTo.SQLite() extension method to <see cref="LoggerConfiguration" />.
    /// </summary>
    public static class LoggerConfigurationSQLiteExtensions
    {
        /// <summary>
        ///     Adds a sink that writes log events to a SQLite database.
        /// </summary>
        /// <param name="loggerConfiguration">The logger configuration.</param>
        /// <param name="sqliteDbPath">The path of SQLite db.</param>
        /// <param name="tableName">The name of the SQLite table to store log.</param>
        /// <param name="restrictedToMinimumLevel">The minimum log event level required in order to write an event to the sink.</param>
        /// <param name="formatProvider">Supplies culture-specific formatting information, or null.</param>
        /// <param name="storeTimestampInUtc">Store timestamp in UTC format</param>
        /// <param name="retentionPeriod">The maximum time that a log entry will be kept in the database, or null to disable automatic deletion of old log entries. Non-null values smaller than 30 minute will be replaced with 30 minute.</param>
        /// <param name="retentionCheckInterval">Time period to execute TTL process. Time span should be in 15 minutes increment</param>
        /// <param name="batchSize">Number of messages to save as batch to database. Default is 100, max 1000</param>
        /// <param name="levelSwitch">
        /// A switch allowing the pass-through minimum level to be changed at runtime.
        /// </param>
        /// <param name="maxDatabaseSize">Maximum database file size can grow in MB. Default 10 MB, maximum 20 GB</param>
        /// <param name="rollOver">If file size grows past max database size, creating rolling backup</param>
        /// <param name="journalMode">SQLite journal mode. Default is WAL for crash-safe writes with concurrent readers; pick Memory for max throughput at the cost of corruption risk on crash.</param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException">A required parameter is null.</exception>
        public static LoggerConfiguration SQLite(
            this LoggerSinkConfiguration loggerConfiguration,
            string sqliteDbPath,
            string tableName = "Logs",
            LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
            IFormatProvider formatProvider = null,
            bool storeTimestampInUtc = false,
            TimeSpan? retentionPeriod = null,
            TimeSpan? retentionCheckInterval = null,
            LoggingLevelSwitch levelSwitch = null,
            uint batchSize = 100,
            uint maxDatabaseSize = 10,
            bool rollOver = true,
            SqliteJournalMode journalMode = SqliteJournalMode.Wal)
        {
            if (loggerConfiguration == null)
            {
                SelfLog.WriteLine("Logger configuration is null");
                throw new ArgumentNullException(nameof(loggerConfiguration));
            }

            if (string.IsNullOrEmpty(sqliteDbPath))
            {
                SelfLog.WriteLine("Invalid sqliteDbPath");
                throw new ArgumentNullException(nameof(sqliteDbPath));
            }

            if (!Path.IsPathRooted(sqliteDbPath))
            {
                sqliteDbPath = Path.Combine(AppContext.BaseDirectory, sqliteDbPath);
            }

            try
            {
                var sqliteDbFile = new FileInfo(sqliteDbPath);
                sqliteDbFile.Directory?.Create();

                return loggerConfiguration.Sink(
                    new SQLiteSink(
                        sqliteDbFile.FullName,
                        tableName,
                        formatProvider,
                        storeTimestampInUtc,
                        retentionPeriod,
                        retentionCheckInterval,
                        batchSize,
                        maxDatabaseSize,
                        rollOver,
                        journalMode),
                    restrictedToMinimumLevel,
                    levelSwitch);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine(ex.Message);
                throw;
            }
        }
    }
}
