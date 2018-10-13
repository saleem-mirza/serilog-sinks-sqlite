// Copyright 2018 Zethian Inc.
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
        private const int MaxSupportedBufferSize = 100_000;
        private const int MaxSupportedBatchSize = 1_000;
        private int _numMessages;
        private bool _canStop;
        private readonly int _maxBufferSize;
        private readonly int _batchSize;
        
        private readonly ConcurrentQueue<LogEvent> _logEventBatch;       
        private readonly BlockingCollection<IList<LogEvent>> _batchEventsCollection;
        private readonly BlockingCollection<LogEvent> _eventsCollection;
        
        private readonly TimeSpan _timerThresholdSpan = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _transientThresholdSpan = TimeSpan.FromSeconds(5);
        
        private readonly Task _timerTask;
        private readonly Task _batchTask;
        private readonly Task _eventPumpTask;
        
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly AutoResetEvent _timerResetEvent = new AutoResetEvent(false);
        private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);


        protected BatchProvider(int batchSize = 100, int maxBufferSize = 25_000)
        {
            _maxBufferSize = Math.Min(Math.Max(5_000, maxBufferSize), MaxSupportedBufferSize);
            _batchSize     = Math.Min(Math.Max(batchSize, 1), MaxSupportedBatchSize);

            _logEventBatch         = new ConcurrentQueue<LogEvent>();
            _batchEventsCollection = new BlockingCollection<IList<LogEvent>>();
            _eventsCollection      = new BlockingCollection<LogEvent>(maxBufferSize);

            _batchTask     = Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
            _timerTask     = Task.Factory.StartNew(TimerPump, TaskCreationOptions.LongRunning);
            _eventPumpTask = Task.Factory.StartNew(EventPump, TaskCreationOptions.LongRunning);
        }

        private async Task Pump()
        {
            try {
                while (true) {
                    var logEvents = _batchEventsCollection.Take(_cancellationTokenSource.Token);
                    SelfLog.WriteLine($"Sending batch of {logEvents.Count} logs");
                    var retValue = await WriteLogEventAsync(logEvents).ConfigureAwait(false);
                    if (retValue) {
                        Interlocked.Add(ref _numMessages, -1 * logEvents.Count);
                    }
                    else {
                        SelfLog.WriteLine($"Retrying after {_transientThresholdSpan.TotalSeconds} seconds...");

                        await Task.Delay(_transientThresholdSpan).ConfigureAwait(false);

                        _batchEventsCollection.Add(logEvents);
                    }

                    if (_cancellationTokenSource.IsCancellationRequested) {
                        _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    }
                }
            }
            catch (OperationCanceledException) {
                SelfLog.WriteLine("Shutting down batch processing");
            }
            catch (Exception e) {
                SelfLog.WriteLine(e.Message);
            }
        }

        private void TimerPump()
        {
            while (!_canStop) {
                _timerResetEvent.WaitOne(_timerThresholdSpan);
                FlushLogEventBatch();
            }
        }

        private void EventPump()
        {
            try {
                while (true) {
                    var logEvent = _eventsCollection.Take(_cancellationTokenSource.Token);
                    _logEventBatch.Enqueue(logEvent);

                    if (_logEventBatch.Count >= _batchSize) {
                        FlushLogEventBatch();
                    }
                }
            }
            catch (OperationCanceledException) {
                SelfLog.WriteLine("Shutting down event pump");
            }
            catch (Exception e) {
                SelfLog.WriteLine(e.Message);
            }
        }

        private void FlushLogEventBatch()
        {
            try {
                _semaphoreSlim.Wait(_cancellationTokenSource.Token);

                if (!_logEventBatch.Any()) {
                    return;
                }

                var logEventBatchSize = _logEventBatch.Count >= _batchSize ? _batchSize : _logEventBatch.Count;
                var logEventList = new List<LogEvent>();

                for (var i = 0; i < logEventBatchSize; i++) {
                    if (_logEventBatch.TryDequeue(out LogEvent logEvent)) {
                        logEventList.Add(logEvent);
                    }
                }

                _batchEventsCollection.Add(logEventList);
            }
            finally {
                if (!_cancellationTokenSource.IsCancellationRequested) {
                    _semaphoreSlim.Release();
                }
            }
        }

        protected void PushEvent(LogEvent logEvent)
        {
            if (_numMessages > _maxBufferSize)
                return;

            _eventsCollection.Add(logEvent);
            Interlocked.Increment(ref _numMessages);
        }

        protected abstract Task<bool> WriteLogEventAsync(ICollection<LogEvent> logEventsBatch);

        #region IDisposable Support

        private bool _disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue) {
                if (disposing) {
                    FlushAndCloseEventHandlers();
                    _semaphoreSlim.Dispose();
                    
                    SelfLog.WriteLine("Sink halted successfully.");
                }

                _disposedValue = true;
            }
        }

        private void FlushAndCloseEventHandlers()
        {
            try {
                SelfLog.WriteLine("Halting sink...");

                _canStop = true;
                _timerResetEvent.Set();

                // Flush events collection
                while (_eventsCollection.TryTake(out LogEvent logEvent)) {
                    _logEventBatch.Enqueue(logEvent);

                    if (_logEventBatch.Count >= _batchSize) {
                        FlushLogEventBatch();
                    }
                }

                FlushLogEventBatch();

                // request cancellation of all tasks
                _cancellationTokenSource.Cancel();

                // Flush events batch
                while (_batchEventsCollection.TryTake(out var eventBatch)) {
                    SelfLog.WriteLine($"Sending batch of {eventBatch.Count} logs");
                    WriteLogEventAsync(eventBatch).Wait(TimeSpan.FromSeconds(30));
                }

                Task.WaitAll(new[] {_eventPumpTask, _batchTask, _timerTask}, TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) {
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
