using System;
using System.CodeDom;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.Batch
{
    internal abstract class BatchProvider : IDisposable
    {
        private readonly uint _batchSize;
        private readonly Task _timerTask;
        private readonly List<Thread> _workerThreads;
        private readonly List<LogEvent> _logEventBatch;
        private readonly BlockingCollection<IList<LogEvent>> _messageQueue;
        private readonly TimeSpan _thresholdTimeSpan = TimeSpan.FromSeconds(10);
        private readonly AutoResetEvent _timerResetEvent = new AutoResetEvent(false);
        private readonly CancellationTokenSource _cancellationToken = new CancellationTokenSource();

        private bool _canStop;

        protected BatchProvider(uint batchSize = 100) : this(batchSize, 1)
        {
        }

        protected BatchProvider(uint batchSize, int nThreads = 1)
        {
            _batchSize = batchSize;
            _logEventBatch = new List<LogEvent>();
            _messageQueue = new BlockingCollection<IList<LogEvent>>();
            _workerThreads = new List<Thread>();

            var maxThreads = Math.Max(1, Math.Min(nThreads, Environment.ProcessorCount));
            for (var i = 0; i < maxThreads; i++)
            {
                var workerThread = new Thread(Pump)
                {
                    IsBackground = true
                };
                workerThread.Start();
                _workerThreads.Add(workerThread);
            }

            _timerTask = Task.Factory.StartNew(TimerPump);
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
                    foreach (var workerThread in _workerThreads)
                        workerThread.Join();
                    SelfLog.WriteLine("Sink halted successfully.");
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion
    }
}