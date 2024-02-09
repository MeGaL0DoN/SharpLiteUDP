namespace SharpLiteUDP.Extensions
{
    public static class ArrayExtensions
    {
        public static byte[] CombineArrays(params byte[][] arrays)
        {
            int totalLength = arrays.Sum(arr => arr.Length);
            var newArray = new byte[totalLength];

            int offset = 0;
            foreach (byte[] arr in arrays)
            {
                Buffer.BlockCopy(arr, 0, newArray, offset, arr.Length);
                offset += arr.Length;
            }

            return newArray;
        }
        public static byte[] Slice(this byte[] originalArr, int startInd, int endInd)
        {
            int len = endInd - startInd;
            var newArr = new byte[len];

            Buffer.BlockCopy(originalArr, startInd, newArr, 0, len);
            return newArr;
        }
    }

    public static class WaitExtensions
    {
        public static Task WaitOneAsync(this WaitHandle handle, CancellationToken token)
        {
            return WaitOneAsync(handle, Timeout.InfiniteTimeSpan, token);
        }

        public static Task<bool> WaitOneAsync(this WaitHandle handle, int timeout, CancellationToken token)
        {
            return WaitOneAsync(handle, TimeSpan.FromMilliseconds(timeout), token);
        }

        public static Task<bool> WaitOneAsync(this WaitHandle handle, TimeSpan timeout, CancellationToken token)
        {
            _ = handle ?? throw new ArgumentNullException(nameof(handle));

            // Handle synchronous cases.
            var alreadySignaled = handle.WaitOne(0);
            if (alreadySignaled)
                return Task.FromResult(true);
            if (timeout == TimeSpan.Zero)
                return Task.FromResult(false);
            if (token.IsCancellationRequested)
                return Task.FromCanceled<bool>(token);

            // Register all asynchronous cases.
            return DoFromWaitHandle(handle, timeout, token);
        }

        private static async Task<bool> DoFromWaitHandle(WaitHandle handle, TimeSpan timeout, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (new ThreadPoolRegistration(handle, timeout, tcs))
            using (token.Register(state => ((TaskCompletionSource<bool>)state).TrySetCanceled(), tcs, useSynchronizationContext: false))
                return await tcs.Task.ConfigureAwait(false);
        }

        private sealed class ThreadPoolRegistration : IDisposable
        {
            private readonly RegisteredWaitHandle _registeredWaitHandle;

            public ThreadPoolRegistration(WaitHandle handle, TimeSpan timeout, TaskCompletionSource<bool> tcs)
            {
                _registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(handle,
                    (state, timedOut) => ((TaskCompletionSource<bool>)state).TrySetResult(!timedOut), tcs,
                    timeout, executeOnlyOnce: true);
            }

            void IDisposable.Dispose() => _registeredWaitHandle.Unregister(null);
        }
    }
}