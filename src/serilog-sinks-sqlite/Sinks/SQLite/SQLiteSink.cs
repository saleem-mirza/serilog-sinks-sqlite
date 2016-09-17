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
using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.SQLite
{
    internal class SQLiteSink : ILogEventSink, IDisposable
    {
        private readonly string _tableName;
        private readonly string _sqliteDbPath;
        private readonly bool _storeTimestampInUtc;

        private readonly Thread _workerThread;
        private readonly IFormatProvider _formatProvider;
        private readonly BlockingCollection<LogEvent> _messageQueue;
        private readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();

        private SqliteCommand _sqlCommand;
        private SqliteConnection _sqlConnection;

        public SQLiteSink(string sqlLiteDbPath,
            string tableName,
            IFormatProvider formatProvider,
            bool storeTimestampInUtc)
        {

            _sqliteDbPath = sqlLiteDbPath;
            _tableName = tableName;
            _formatProvider = formatProvider;
            _storeTimestampInUtc = storeTimestampInUtc;
            _messageQueue = new BlockingCollection<LogEvent>();

            InitializeDatabase();

            _workerThread = new Thread(async () =>
            {
                LogEvent logEvent = null;
                try
                {
                    while (true)
                    {
                        logEvent = _messageQueue.Take(_cancellationToken.Token);
                        await WriteLogEvent(logEvent);
                    }
                }
                catch (OperationCanceledException e)
                {
                    foreach (var msg in _messageQueue)
                    {
                        await WriteLogEvent(logEvent);
                    }

                    SelfLog.WriteLine(e.Message);
                }
                catch (Exception e)
                {
                    SelfLog.WriteLine(e.Message);
                }
            })
            { IsBackground = true };
            _workerThread.Start();
        }

        private void InitializeDatabase()
        {
            _sqlConnection = new SqliteConnection($"Data Source={_sqliteDbPath}");
            _sqlConnection.Open();

            CreateSqlTable(_sqlConnection);
            _sqlCommand = CreateSqlInsertCommand(_sqlConnection);
        }

        private void CreateSqlTable(SqliteConnection sqlConnection)
        {
            var colDefs = "id INTEGER PRIMARY KEY AUTOINCREMENT,";
            colDefs += "Timestamp TEXT,";
            colDefs += "Level VARCHAR(10),";
            colDefs += "Exception TEXT,";
            colDefs += "RenderedMessage TEXT,";
            colDefs += "Properties TEXT";

            var sqlCreateText = $"CREATE TABLE IF NOT EXISTS {_tableName} ({colDefs})";

            var sqlCommand = new SqliteCommand(sqlCreateText, sqlConnection);
            sqlCommand.ExecuteNonQuery();
        }

        private SqliteCommand CreateSqlInsertCommand(SqliteConnection connection)
        {
            var sqlInsertText = "INSERT INTO {0} (Timestamp, Level, Exception, RenderedMessage, Properties)";
            sqlInsertText += " VALUES (@timeStamp, @level, @exception, @renderedMessage, @properties)";
            sqlInsertText = string.Format(sqlInsertText, _tableName);

            var sqlCommand = _sqlConnection.CreateCommand();
            sqlCommand.CommandText = sqlInsertText;
            sqlCommand.CommandType = CommandType.Text;

            sqlCommand.Parameters.Add(new SqliteParameter("@timeStamp", DbType.DateTime2));
            sqlCommand.Parameters.Add(new SqliteParameter("@level", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@exception", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@renderedMessage", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@properties", DbType.String));

            sqlCommand.Prepare();
            return sqlCommand;
        }

        private async Task WriteLogEvent(LogEvent logEvent)
        {
            try
            {
                _sqlCommand.Parameters["@timeStamp"].Value = _storeTimestampInUtc ? logEvent.Timestamp.ToUniversalTime() : logEvent.Timestamp;
                _sqlCommand.Parameters["@level"].Value = logEvent.Level.ToString();
                _sqlCommand.Parameters["@exception"].Value = logEvent.Exception?.ToString() ?? string.Empty;
                _sqlCommand.Parameters["@renderedMessage"].Value = logEvent.RenderMessage(_formatProvider);
                _sqlCommand.Parameters["@properties"].Value = JsonConvert.SerializeObject(logEvent.Properties);

                await _sqlCommand.ExecuteNonQueryAsync();
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
            }
        }

        public void Emit(LogEvent logEvent)
        {
            _messageQueue.Add(logEvent);
        }

        #region IDisposable Support
        private bool _disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _cancellationToken.Cancel();
                    _workerThread.Join();

                    if (_sqlConnection != null && _sqlConnection.State != ConnectionState.Closed)
                    {
                        _sqlConnection.Close();
                    }

                }

                _disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }

        #endregion
    }
}
