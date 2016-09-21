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

using System.Reflection;
using Serilog.Debugging;

namespace Serilog
{
    using System;
    using System.IO;

    using Serilog.Configuration;
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
        /// <exception cref="ArgumentNullException">A required parameter is null.</exception>
        public static LoggerConfiguration SQLite(
            this LoggerSinkConfiguration loggerConfiguration,
            string sqliteDbPath,
            string tableName = "Logs",
            LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
            IFormatProvider formatProvider = null,
            bool storeTimestampInUtc = false)
        {
            if (loggerConfiguration == null)
            {
                throw new ArgumentNullException(nameof(loggerConfiguration));
            }

            if (string.IsNullOrEmpty(sqliteDbPath))
            {
                throw new ArgumentNullException(nameof(sqliteDbPath));
            }

            Uri sqliteDbPathUri;
            if (!Uri.TryCreate(sqliteDbPath, UriKind.RelativeOrAbsolute, out sqliteDbPathUri))
            {
                throw new ArgumentException($"Invalid path {nameof(sqliteDbPath)}");
            }

            if (!sqliteDbPathUri.IsAbsoluteUri)
            {
                sqliteDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, sqliteDbPath);
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
                        storeTimestampInUtc),
                    restrictedToMinimumLevel);
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine(ex.Message);
                throw;
            }
        }
    }
}