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
using System.Data;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.Batch;
using System.Diagnostics;
using System.Linq;
using Serilog.Sinks.Extensions;
using System.Data.SQLite;

namespace Serilog.Sinks.SQLite
{
    internal class SQLiteSink : BatchProvider, ILogEventSink
    {
        private readonly string _connString;
        private readonly IFormatProvider _formatProvider;
        private readonly bool _storeTimestampInUtc;
        private readonly string _tableName;
        private readonly TimeSpan? _retentionPeriod;
        private readonly Stopwatch _retentionWatch = new Stopwatch();

        public SQLiteSink(string sqlLiteDbPath,
            string tableName,
            IFormatProvider formatProvider,
            bool storeTimestampInUtc,
            TimeSpan? retentionPeriod)
        {
            _connString = CreateConnectionString(sqlLiteDbPath);
            _tableName = tableName;
            _formatProvider = formatProvider;
            _storeTimestampInUtc = storeTimestampInUtc;

            if (retentionPeriod.HasValue)
                // impose a min retention period of 1 minute
                _retentionPeriod = new[] { retentionPeriod.Value, TimeSpan.FromMinutes(1) }.Max();
           
            InitializeDatabase();
        }

        private static string CreateConnectionString(string dbPath) =>
            new SQLiteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

        #region ILogEvent implementation

        public void Emit(LogEvent logEvent)
        {
            PushEvent(logEvent);
        }

        #endregion

        private void InitializeDatabase()
        {
            using (var conn = GetSqLiteConnection())
                CreateSqlTable(conn);
        }

        private SQLiteConnection GetSqLiteConnection()
        {
            var sqlConnection = new SQLiteConnection(_connString);
            sqlConnection.Open();
            return sqlConnection;
        }

        private void CreateSqlTable(SQLiteConnection sqlConnection)
        {
            var colDefs = "id INTEGER PRIMARY KEY AUTOINCREMENT,";
            colDefs += "Timestamp TEXT,";
            colDefs += "Level VARCHAR(10),";
            colDefs += "Exception TEXT,";
            colDefs += "RenderedMessage TEXT,";
            colDefs += "Properties TEXT";

            var sqlCreateText = $"CREATE TABLE IF NOT EXISTS {_tableName} ({colDefs})";

            var sqlCommand = new SQLiteCommand(sqlCreateText, sqlConnection);
            sqlCommand.ExecuteNonQuery();
        }

        private SQLiteCommand CreateSqlInsertCommand(SQLiteConnection connection)
        {
            var sqlInsertText = "INSERT INTO {0} (Timestamp, Level, Exception, RenderedMessage, Properties)";
            sqlInsertText += " VALUES (@timeStamp, @level, @exception, @renderedMessage, @properties)";
            sqlInsertText = string.Format(sqlInsertText, _tableName);

            var sqlCommand = connection.CreateCommand();
            sqlCommand.CommandText = sqlInsertText;
            sqlCommand.CommandType = CommandType.Text;

            sqlCommand.Parameters.Add(new SQLiteParameter("@timeStamp", DbType.DateTime2));
            sqlCommand.Parameters.Add(new SQLiteParameter("@level", DbType.String));
            sqlCommand.Parameters.Add(new SQLiteParameter("@exception", DbType.String));
            sqlCommand.Parameters.Add(new SQLiteParameter("@renderedMessage", DbType.String));
            sqlCommand.Parameters.Add(new SQLiteParameter("@properties", DbType.String));

            return sqlCommand;
        }

        protected override void WriteLogEvent(ICollection<LogEvent> logEventsBatch)
        {
            if ((logEventsBatch == null) || (logEventsBatch.Count == 0))
                return;
            try
            {
                using (var sqlConnection = GetSqLiteConnection())
                {
                    ApplyRetentionPolicy(sqlConnection);

                    using (var tr = sqlConnection.BeginTransaction())
                    {
                        using (var sqlCommand = CreateSqlInsertCommand(sqlConnection))
                        {
                            sqlCommand.Transaction = tr;

                            foreach (var logEvent in logEventsBatch)
                            {
                                sqlCommand.Parameters["@timeStamp"].Value = _storeTimestampInUtc
                                    ? logEvent.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")
                                    : logEvent.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss");
                                sqlCommand.Parameters["@level"].Value = logEvent.Level.ToString();
                                sqlCommand.Parameters["@exception"].Value = logEvent.Exception?.ToString() ?? string.Empty;
                                sqlCommand.Parameters["@renderedMessage"].Value = logEvent.MessageTemplate.ToString();

                                sqlCommand.Parameters["@properties"].Value = logEvent.Properties.Count > 0
                                    ? logEvent.Properties.Json()
                                    : string.Empty;

                                sqlCommand.ExecuteNonQuery();
                            }
                        }
                        tr.Commit();
                    }

                    sqlConnection.Close();
                }
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
            }
        }

        private void ApplyRetentionPolicy(SQLiteConnection sqlConnection)
        {
            if (!_retentionPeriod.HasValue)
                // there is no retention policy
                return;

            if (_retentionWatch.IsRunning && _retentionWatch.Elapsed < _retentionPeriod.Value)
                // Besides deleting records older than X 
                // let's only delete records every X often
                // because of the check whether the _retentionWatch is running,
                // the first write operation during this application run
                // will result in deleting old records
                return;

            var epoch = DateTimeOffset.Now.Subtract(_retentionPeriod.Value);
            using (var cmd = CreateSqlDeleteCommand(sqlConnection, epoch))
            {
                SelfLog.WriteLine("Deleting log entries older than {0}", epoch);
                cmd.ExecuteNonQuery();
            }

            _retentionWatch.Restart();
        }

        private SQLiteCommand CreateSqlDeleteCommand(SQLiteConnection sqlConnection, DateTimeOffset epoch)
        {
            var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_tableName} WHERE Timestamp < @epoch";
            cmd.Parameters.Add(new SQLiteParameter("@epoch", DbType.DateTime2)
            {
                Value = (_storeTimestampInUtc ? epoch.ToUniversalTime() : epoch).ToString("yyyy-MM-ddTHH:mm:ss")
            });
            return cmd;
        }
    }
}