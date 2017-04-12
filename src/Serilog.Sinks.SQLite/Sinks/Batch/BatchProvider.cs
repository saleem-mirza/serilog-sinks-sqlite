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
        private readonly List<LogEvent> _logEventBatch;
        private readonly BlockingCollection<IList<LogEvent>> _messageQueue;
        private readonly TimeSpan _thresholdTimeSpan = TimeSpan.FromSeconds(10);
        private readonly AutoResetEvent _timerResetEvent = new AutoResetEvent(false);
        private readonly Task _timerTask;
        private readonly Task _eventPumpTask;
        private readonly Task _cleanupTask;
        private readonly List<Task> _workerTasks = new List<Task>();

        private bool _canStop;

        protected BatchProvider(uint batchSize = 100, int nThreads = 1)
        {
            _batchSize = batchSize;
            _logEventBatch = new List<LogEvent>();
            _messageQueue = new BlockingCollection<IList<LogEvent>>();

            _eventPumpTask = Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
            _timerTask = Task.Factory.StartNew(TimerPump, TaskCreationOptions.LongRunning);
            _cleanupTask = new Task(() =>
            {
                _canStop = true;
                _timerResetEvent.Set();
                _timerTask.Wait();

                IList<LogEvent> eventBatch;
                while (_messageQueue.TryTake(out eventBatch))
                    WriteLogEvent(eventBatch);

                FlushLogEventBatch();
            });
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    var logEvents = _messageQueue.Take(_cancellationToken.Token);
                    var task = Task.Factory.StartNew((x) => { WriteLogEvent(x as IList<LogEvent>); }, logEvents);

                    _workerTasks.Add(task);

                    if (_workerTasks.Count <= 32) continue;
                    Task.WaitAll(_workerTasks.ToArray(), _cancellationToken.Token);
                    _workerTasks.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                FluchAndCloseEvents();
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

        private void FlushLogEventBatch()
        {
            if (!_logEventBatch.Any())
            {
                return;
            }

            lock (this)
            {
                _messageQueue.Add(_logEventBatch.ToArray());
                _logEventBatch.Clear();
            }
        }

        protected void PushEvent(LogEvent logEvent)
        {
            _logEventBatch.Add(logEvent);
            if (_logEventBatch.Count >= _batchSize)
                FlushLogEventBatch();
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
                    SelfLog.WriteLine("Halting sink...");
                    _cancellationToken.Cancel();

                    Task.WaitAll(_workerTasks.ToArray());

                    FluchAndCloseEvents();

                    SelfLog.WriteLine("Sink halted successfully.");
                }

                _disposedValue = true;
            }
        }

        private void FluchAndCloseEvents()
        {
            if (_cleanupTask.Status != TaskStatus.Created)
            {
                return;
            }

            try
            {
                _cleanupTask.RunSynchronously();
                _cleanupTask.Wait(TimeSpan.FromSeconds(30));

                Task.WaitAll(new[] {_eventPumpTask, _timerTask}, TimeSpan.FromSeconds(30));
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