// Copyright 2016 Zethian Inc.
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
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Serilog.Sinks.Batch
{
    internal abstract class BatchProvider : IDisposable
    {
        private readonly uint _batchSize;
        private readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();
        private readonly CancellationTokenSource _eventCancellationToken = new CancellationTokenSource();
        private readonly ConcurrentQueue<LogEvent> _logEventBatch;
        private readonly BlockingCollection<IList<LogEvent>> _batchEventsCollection;
        private readonly BlockingCollection<LogEvent> _eventsCollection;
        private readonly TimeSpan _thresholdTimeSpan = TimeSpan.FromSeconds(10);
        private readonly AutoResetEvent _timerResetEvent = new AutoResetEvent(false);
        private readonly Task _timerTask;
        private readonly Task _batchTask;
        private readonly Task _eventPumpTask;
        private readonly List<Task> _workerTasks = new List<Task>();

        private bool _canStop;

        protected BatchProvider(uint batchSize = 100, int nThreads = 1)
        {
            _batchSize = batchSize == 0 ? 1: batchSize;
            _logEventBatch = new ConcurrentQueue<LogEvent>();
            _batchEventsCollection = new BlockingCollection<IList<LogEvent>>();
            _eventsCollection = new BlockingCollection<LogEvent>();

            _batchTask = Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
            _timerTask = Task.Factory.StartNew(TimerPump, TaskCreationOptions.LongRunning);
            _eventPumpTask = Task.Factory.StartNew(EventPump, TaskCreationOptions.LongRunning);
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    var logEvents = _batchEventsCollection.Take(_cancellationToken.Token);
                    var task = Task.Factory.StartNew((x) => { WriteLogEvent(x as IList<LogEvent>); }, logEvents);

                    _workerTasks.Add(task);

                    if (_workerTasks.Count <= 32) continue;
                    Task.WaitAll(_workerTasks.ToArray(), _cancellationToken.Token);
                    _workerTasks.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                SelfLog.WriteLine("Shutting down batch processing");
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
            }
        }

        private void TimerPump()
        {
            while (!_canStop)
            {
                _timerResetEvent.WaitOne(_thresholdTimeSpan);                
                FlushLogEventBatch();
            }
        }

        private void EventPump()
        {
            try
            {
                while (true)
                {
                    var logEvent = _eventsCollection.Take(_eventCancellationToken.Token);
                    _logEventBatch.Enqueue(logEvent);

                    if(_logEventBatch.Count >= _batchSize)
                    {
                        FlushLogEventBatch();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SelfLog.WriteLine("Shutting down event pump");
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(e.Message);
            }
        }

        private void FlushLogEventBatch()
        {
            if (!_logEventBatch.Any())
            {
                return;
            }

            var logEventBatchSize = _logEventBatch.Count >= _batchSize ? (int)_batchSize : _logEventBatch.Count;
            var logEventList = new List<LogEvent>();

            for (int i = 0; i < logEventBatchSize; i++)
            {
                if(_logEventBatch.TryDequeue(out LogEvent logEvent))
                {
                    logEventList.Add(logEvent);
                }
            }
            _batchEventsCollection.Add(logEventList);
        }

        protected void PushEvent(LogEvent logEvent)
        {
            _eventsCollection.Add(logEvent);
        }

        protected abstract void WriteLogEvent(ICollection<LogEvent> logEventsBatch);

        #region IDisposable Support

        private bool _disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    FluchAndCloseEventHandlers();

                    SelfLog.WriteLine("Sink halted successfully.");
                }

                _disposedValue = true;
            }
        }

        private void FluchAndCloseEventHandlers()
        {
            try
            {
                SelfLog.WriteLine("Halting sink...");

                _eventCancellationToken.Cancel();
                _cancellationToken.Cancel();

                Task.WaitAll(_workerTasks.ToArray());

                _canStop = true;
                _timerResetEvent.Set();

                // Flush events collection
                while (_eventsCollection.TryTake(out LogEvent logEvent))
                {
                    _logEventBatch.Enqueue(logEvent);

                    if (_logEventBatch.Count >= _batchSize)
                    {
                        FlushLogEventBatch();
                    }
                }

                FlushLogEventBatch();

                // Flush events batch
                while (_batchEventsCollection.TryTake(out IList<LogEvent> eventBatch))
                    WriteLogEvent(eventBatch);

                Task.WaitAll(new[] { _eventPumpTask, _batchTask, _timerTask}, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                SelfLog.WriteLine(ex.Message);
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}