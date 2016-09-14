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
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.SQLite
{
    public class SQLiteSink : ILogEventSink, IDisposable
    {
        private string _sqliteDbPath;
        private string _tableName;
        private IFormatProvider _formatProvider;
        private bool _storeTimestampInUtc;

        private SQLiteConnection _sqlConnection;
        private SQLiteCommand _sqlCommand;
        private BlockingCollection<LogEvent> _messageQueue;
        private Thread _workerThread;
        private CancellationTokenSource _cancellationToken = new CancellationTokenSource();

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

            _workerThread = new Thread( async () =>
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
                catch(Exception e)
                {
                    SelfLog.WriteLine(e.Message);
                }
            });

            _workerThread.Start();
        }

        private void InitializeDatabase()
        {
            _sqlConnection = new SQLiteConnection(string.Format("Data Source={0};Version=3", _sqliteDbPath));
            _sqlConnection.Open();

            CreateSQLTable(_sqlConnection);
            _sqlCommand = CreateSQLInsertCommand(_sqlConnection);
        }

        private void CreateSQLTable(SQLiteConnection sqlConnection)
        {
            var colDefs = "id INTEGER PRIMARY KEY AUTOINCREMENT,";
            colDefs += "Timestamp DATETIME,";
            colDefs += "UTimestamp NUMERIC,";
            colDefs += "Level VARCHAR(10),";
            colDefs += "Exception TEXT,";
            colDefs += "RenderedMessage TEXT,";
            colDefs += "Properties TEXT";

            var sqlCreateText = string.Format("CREATE TABLE IF NOT EXISTS {0} ({1})", _tableName, colDefs);

            var sqlCommand = new SQLiteCommand(sqlConnection);
            sqlCommand.CommandText = sqlCreateText;
            sqlCommand.ExecuteNonQuery();
        }

        private SQLiteCommand CreateSQLInsertCommand(SQLiteConnection connection)
        {
            var _sqlInsertText = "INSERT INTO {0} (Timestamp, UTimestamp, Level, Exception, RenderedMessage, Properties)";
            _sqlInsertText += " VALUES (@timeStamp, @uTimeStamp, @level, @exception, @renderedMessage, @properties)";
            _sqlInsertText = string.Format(_sqlInsertText, _tableName);

            var sqlCommand = new SQLiteCommand(connection);
            sqlCommand.CommandText = _sqlInsertText;
            sqlCommand.Parameters.Add(new SQLiteParameter("@TimeStamp", DbType.DateTime));
            sqlCommand.Parameters.Add(new SQLiteParameter("@uTimeStamp", DbType.UInt64));
            sqlCommand.Parameters.Add(new SQLiteParameter("@level", DbType.String));
            sqlCommand.Parameters.Add(new SQLiteParameter("@exception", DbType.String));
            sqlCommand.Parameters.Add(new SQLiteParameter("@renderedMessage", DbType.String));
            sqlCommand.Parameters.Add(new SQLiteParameter("@properties", DbType.String));

            sqlCommand.Prepare();
            return sqlCommand;
        }

        private Data.LogEvent ConvertLogEventToDataEvent(LogEvent logEvent)
        {
            return new Data.LogEvent(
                logEvent,
                logEvent.RenderMessage(_formatProvider),
                _storeTimestampInUtc);
        }

        private async Task WriteLogEvent(LogEvent logEvent)
        {
            try
            {
                var dataLogEvent = ConvertLogEventToDataEvent(logEvent);

                var eventTime = dataLogEvent.Timestamp.Ticks;

                _sqlCommand.Parameters["@TimeStamp"].Value = new DateTime(eventTime);
                _sqlCommand.Parameters["@uTimeStamp"].Value = eventTime;
                _sqlCommand.Parameters["@level"].Value = dataLogEvent.Level;
                if (dataLogEvent.Exception != null)
                {
                    _sqlCommand.Parameters["@exception"].Value = dataLogEvent.Exception.ToString();
                }
                _sqlCommand.Parameters["@renderedMessage"].Value = dataLogEvent.RenderedMessage;
                _sqlCommand.Parameters["@properties"].Value = JsonConvert.SerializeObject(dataLogEvent.Properties);

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
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
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

                disposedValue = true;
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
