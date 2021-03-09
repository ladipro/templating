// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.TemplateEngine.Edge.Settings
{
    /// <summary>
    /// Helper class to work with <see cref="Mutex"/> in <c>async</c> method, since <c>await</c>
    /// can switch to different thread and <see cref="Mutex.ReleaseMutex"/> must be called from same thread.
    /// Hence this helper class.
    /// </summary>
    internal sealed class AsyncMutex : IDisposable
    {
        private readonly TaskCompletionSource<IDisposable> _taskCompletionSource;
        private readonly ManualResetEvent _blockReleasingMutex = new ManualResetEvent(false);
        private readonly ManualResetEvent _cancelRequestedEvent = new ManualResetEvent(false);
        private readonly string _mutexName;
        private readonly CancellationToken _token;
        private bool _disposed;

        private AsyncMutex(string mutexName, CancellationToken token)
        {
            _mutexName = mutexName;
            _token = token;
            _taskCompletionSource = new TaskCompletionSource<IDisposable>();

            _token.Register(() => _cancelRequestedEvent.Set());

            var thread = new Thread(new ThreadStart(WaitLoop));
            thread.IsBackground = true;
            thread.Start();
            thread.Name = "TemplateEngine AsyncMutex";
        }

        public static Task<IDisposable> WaitAsync(string mutexName, CancellationToken token)
        {
            var mutex = new AsyncMutex(mutexName, token);
            return mutex._taskCompletionSource.Task;
        }

        private void WaitLoop()
        {
            var mutex = new Mutex(false, _mutexName);

            int signaledHandle = WaitHandle.WaitAny(new WaitHandle[] { mutex, _cancelRequestedEvent });
            if (signaledHandle == 1)
            {
                Debug.Assert(_token.IsCancellationRequested);
                mutex.ReleaseMutex();
                _taskCompletionSource.SetCanceled();
                _blockReleasingMutex.Dispose();
                return;
            }
            Debug.Assert(signaledHandle == 0);

            _taskCompletionSource.SetResult(this);
            _blockReleasingMutex.WaitOne();
            _blockReleasingMutex.Dispose();
            mutex.ReleaseMutex();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _blockReleasingMutex.Set();
        }
    }
}
