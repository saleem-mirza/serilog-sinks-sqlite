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
        private const int BatchSize = 250;
        private bool _canStop;

        private readonly string _tableName;
        private readonly string _sqliteDbPath;
        private readonly bool _storeTimestampInUtc;

        private readonly Task _timerTask;
        private readonly Thread _workerThread;
        private readonly IFormatProvider _formatProvider;
        private readonly List<LogEvent> _logEventBatch;
        private readonly BlockingCollection<LogEvent> _messageQueue;
        private readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();
        private readonly TimeSpan _thresholdTimeSpan = TimeSpan.FromSeconds(10);

        private readonly AutoResetEvent _timerResetEvent = new AutoResetEvent(false);
        private readonly ManualResetEventSlim _emitResetEvent = new ManualResetEventSlim(true);

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
            _logEventBatch = new List<LogEvent>();

            InitializeDatabase();

            _workerThread = new Thread(Pump)
            {
                IsBackground = true
            };
            _workerThread.Start();

            _timerTask = Task.Factory.StartNew(TimerPump);
        }

        private void TimerPump()
        {
            while (!_canStop)
            {
                _timerResetEvent.WaitOne(_thresholdTimeSpan);
                _emitResetEvent.Reset();
                try
                {
                    Task.Delay(500);
                    if (_logEventBatch.Count <= 0)
                    {
                        continue;
                    }

                    FlushLogEventBatch();
                }
                finally
                {
                    _emitResetEvent.Set();
                }
            }
        }

        private void FlushLogEventBatch()
        {
            lock (this)
            {
                WriteLogEvent(_logEventBatch).Wait();
                _logEventBatch.Clear();
            }
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    var logEvent = _messageQueue.Take(_cancellationToken.Token);

                    _emitResetEvent.Wait();
                    _logEventBatch.Add(logEvent);
                    if (_logEventBatch.Count < BatchSize)
                    {
                        continue;
                    }

                    FlushLogEventBatch();
                }
            }
            catch (OperationCanceledException e)
            {
                _canStop = true;
                _timerResetEvent.Set();
                _emitResetEvent.Set();

                _timerTask.Wait();

                WriteLogEvent(_messageQueue.ToArray()).Wait();
                SelfLog.WriteLine(e.Message);
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
            }
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

            var sqlCommand = connection.CreateCommand();
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

        private async Task WriteLogEvent(ICollection<LogEvent> logEventsBatch)
        {
            if (logEventsBatch == null || logEventsBatch.Count == 0)
            {
                return;
            }

            using (var tr = _sqlConnection.BeginTransaction())
            {
                try
                {
                    _sqlCommand.Transaction = tr;
                    foreach (var logEvent in logEventsBatch)
                    {
                        await WriteLogEvent(logEvent);
                    }

                    tr.Commit();
                }
                finally
                {
                    _sqlCommand.Transaction = null;
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            _messageQueue.Add(logEvent);
        }

        #region IDisposable Support
        private bool _disposedValue; // To detect redundant calls

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
