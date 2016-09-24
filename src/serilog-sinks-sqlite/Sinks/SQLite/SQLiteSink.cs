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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Extensions;

namespace Serilog.Sinks.SQLite
{
    internal class SQLiteSink : ILogEventSink, IDisposable
    {
        private readonly IFormatProvider _formatProvider;
        private readonly string _sqliteDbPath;
        private readonly bool _storeTimestampInUtc;
        private readonly string _tableName;

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
            _messageQueue = new BlockingCollection<IList<LogEvent>>();
            _logEventBatch = new List<LogEvent>();

            InitializeDatabase();

            for (var i = 0; i < 1; i++)
            {
                var workerThread = new Thread(Pump)
                {
                    IsBackground = true,
                };
                workerThread.Start();
                _workerThreads.Add(workerThread);
            }

            _timerTask = Task.Factory.StartNew(TimerPump);
        }

        #region ILogEvent implementation

        public void Emit(LogEvent logEvent)
        {
            _logEventBatch.Add(logEvent);
            if (_logEventBatch.Count >= BatchSize)
                FlushLogEventBatch();
        }

        #endregion

        private void InitializeDatabase()
        {
            _sqlConnection = GetSqLiteConnection();

            CreateSqlTable(_sqlConnection);
        }

        private SqliteConnection GetSqLiteConnection()
        {
            var sqlConnection = new SqliteConnection($"Data Source={_sqliteDbPath}");
            sqlConnection.Open();
            return sqlConnection;
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

            return sqlCommand;
        }

        #region Parallel and Buffered implementation

        private const int BatchSize = 500;
        private bool _canStop;

        private readonly Task _timerTask;
        private readonly List<Thread> _workerThreads = new List<Thread>();
        private readonly List<LogEvent> _logEventBatch;
        private readonly BlockingCollection<IList<LogEvent>> _messageQueue;
        private readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();
        private readonly TimeSpan _thresholdTimeSpan = TimeSpan.FromSeconds(10);
        private readonly AutoResetEvent _timerResetEvent = new AutoResetEvent(false);

        private void TimerPump()
        {
            while (!_canStop)
            {
                _timerResetEvent.WaitOne(_thresholdTimeSpan);
                FlushLogEventBatch();
            }
        }

        private void FlushLogEventBatch()
        {
            lock (this)
            {
                _messageQueue.Add(_logEventBatch.ToArray());
                _logEventBatch.Clear();
            }
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    var logEvents = _messageQueue.Take(_cancellationToken.Token);
                    WriteLogEvent(logEvents);
                }
            }
            catch (OperationCanceledException)
            {
                _canStop = true;
                _timerResetEvent.Set();
                _timerTask.Wait();

                IList<LogEvent> eventBatch;
                while (_messageQueue.TryTake(out eventBatch))
                    WriteLogEvent(eventBatch);
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
            }
        }

        private void WriteLogEvent(ICollection<LogEvent> logEventsBatch)
        {
            if ((logEventsBatch == null) || (logEventsBatch.Count == 0))
                return;
            try
            {

                using (var sqlConnection = GetSqLiteConnection())
                {
                    using (var tr = sqlConnection.BeginTransaction())
                    {
                        var sqlCommand = CreateSqlInsertCommand(sqlConnection);
                        sqlCommand.Transaction = tr;

                        foreach (var logEvent in logEventsBatch)
                        {
                            sqlCommand.Parameters["@timeStamp"].Value = _storeTimestampInUtc
                                ? logEvent.Timestamp.ToUniversalTime()
                                : logEvent.Timestamp;
                            sqlCommand.Parameters["@level"].Value = logEvent.Level.ToString();
                            sqlCommand.Parameters["@exception"].Value = logEvent.Exception?.ToString() ?? string.Empty;
                            sqlCommand.Parameters["@renderedMessage"].Value = logEvent.MessageTemplate.ToString();

                            sqlCommand.Parameters["@properties"].Value = logEvent.Properties.Count > 0
                                ? logEvent.Properties.Json()
                                : string.Empty;

                            sqlCommand.ExecuteNonQuery();
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

        #endregion

        #region IDisposable Support

        private bool _disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _cancellationToken.Cancel();
                    foreach (var workerThread in _workerThreads)
                    {
                        workerThread.Join();
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