using System.Net.Sockets;
using System.Net;
using System.Text;

namespace SharpLiteUDP
{
    class ConnectionInfo
    {
        public DateTime LastMessageTime { get; set; } = DateTime.UtcNow;
    }

    record struct PacketInfo(IPEndPoint EndPoint, uint SequenceNumber);

    public class UdpPeer : IDisposable, IAsyncDisposable
    {
        public record class Request(UdpPeer udpPeer, IPEndPoint EndPoint, string ConnectionKey)
        {
            public void Accept() => _ = udpPeer.AcceptConnection(this);
        }

        public delegate void NetDisconnect(IPEndPoint endPoint);
        public delegate void NetData(byte[] buffer, IPEndPoint sender, bool isReliable);
        public delegate void NetConnectionRequest(Request request);
        public delegate void NetConnect(IPEndPoint endPoint, string? connectionKey);

        private readonly UdpClient _client;
        private CancellationTokenSource cancellationTokenSource = new();
        private CancellationToken cancellationToken;
        private IPEndPoint? hostEndPoint { get; set; }

        private uint SequenceNumber;
        private HashSet<PacketInfo> NotAcknowledgedPackets = new();
        private AutoResetEvent PacketAcknowledgedSignal = new(false);
        private HashSet<PacketInfo> ReceivedPackets { get; set; } = new();
        private Dictionary<IPEndPoint, ConnectionInfo> ConnectionInfos { get; set; } = new();

        public int DisconnectionTimeOut { get; init; } = 2000;
        public IReadOnlyCollection<IPEndPoint> Connections => ConnectionInfos.Keys;

        public event NetDisconnect? OnDisconnect;
        public event NetData? OnDataReceive;
        public event NetConnectionRequest? OnConnectionRequest;
        public event NetConnect? OnConnected;

        private const int SIO_UDP_CONNRESET = -1744830452;
        private void disableUDPException()
        {
            if (OperatingSystem.IsWindows())
                _client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }

        public UdpPeer(int port)
        {
            _client = new UdpClient(port);
            Init();
        }
        public UdpPeer()
        {
            _client = new UdpClient(0);
            Init();
        }

        private void Init()
        {
            disableUDPException();
            cancellationToken = cancellationTokenSource.Token;
        }

        public void Start(bool RunThreaded = false)
        {
            if (RunThreaded)
            {
                Task.Run(() => CheckForMessages(cancellationToken));
                Task.Run(() => CheckConnections(cancellationToken));
            }
            else
            {
                _ = CheckForMessages(cancellationToken);
                _ = CheckConnections(cancellationToken);
            }
        }
        public async Task StopAsync()
        {
            await SendUnreliableToAllAsync(Array.Empty<byte>(), UdpHeader.Disconnection);

            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();

            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;

            ConnectionInfos.Clear();
            ReceivedPackets.Clear();
            NotAcknowledgedPackets.Clear();
            hostEndPoint = null;
            SequenceNumber = 0;
        }
        public void Stop() => Task.Run(StopAsync).GetAwaiter().GetResult();

        public Task<bool> Connect(IPEndPoint endPoint, string connectionKey)
        {
            if (hostEndPoint != null) throw new Exception("Connection is already established. Call Disconnect first.");
            hostEndPoint = endPoint;

            var buffer = Encoding.UTF8.GetBytes(connectionKey);
            return SendReliableAsync(buffer, hostEndPoint, UdpHeader.ConnectRequest);
        }

        // returns true if packet received first time
        private bool ProcessReliablePacket(byte[] buffer, IPEndPoint endPoint)
        {
            var sequenceNumberBytes = buffer.Slice(0, sizeof(uint));
            uint sequenceNumber = BitConverter.ToUInt32(sequenceNumberBytes);
            _ = SendUnreliableAsync(sequenceNumberBytes, endPoint, UdpHeader.Acknowledgment);

            var msgInfo = new PacketInfo(endPoint, sequenceNumber);
            if (ReceivedPackets.Contains(msgInfo))
                return false;

            ReceivedPackets.Add(msgInfo);
            return true;
        }

        async Task CheckConnections(CancellationToken token)
        {
            int checkDelay = DisconnectionTimeOut / 20;
            int realTimeOut = DisconnectionTimeOut - checkDelay;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(checkDelay));

            var toRemove = new List<IPEndPoint>();
            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync())
            {
                _ = SendUnreliableToAllAsync(Array.Empty<byte>(), UdpHeader.Ping);

                foreach (var con in Connections)
                {
                    if ((DateTime.UtcNow - ConnectionInfos[con].LastMessageTime).TotalMilliseconds >= realTimeOut)
                        toRemove.Add(con);
                }
                foreach (var con in toRemove)
                    ProcessPeerDisconnection(con, true);

                toRemove.Clear();
            }
        }

        private async Task AcceptConnection(Request request)
        {
            var result = await SendReliableAsync(Array.Empty<byte>(), request.EndPoint, UdpHeader.ConnectionAccept);
            if (result)
            {
                ConnectionInfos.Add(request.EndPoint, new ConnectionInfo());
                OnConnected?.Invoke(request.EndPoint, request.ConnectionKey);
            }
        }

        public async Task DisonnectPeer(IPEndPoint endPoint)
        {
            await SendUnreliableAsync(Array.Empty<byte>(), endPoint, UdpHeader.Disconnection);
            ProcessPeerDisconnection(endPoint, false);
        }

        private void ProcessPeerDisconnection(IPEndPoint endPoint, bool callEvent)
        {
            ConnectionInfos.Remove(endPoint);
            ReceivedPackets.RemoveWhere(p => p.EndPoint == endPoint);
            NotAcknowledgedPackets.RemoveWhere(p => p.EndPoint == endPoint);
            PacketAcknowledgedSignal.Set();
            if (callEvent) OnDisconnect?.Invoke(endPoint);
        }

        async Task CheckForMessages(CancellationToken token)
        {
            while (true)
            {
                UdpReceiveResult msg;
                try
                {
                    msg = await _client.ReceiveAsync(token);
                }
                catch
                {
                    return;
                }

                var udpHeader = (UdpHeader)msg.Buffer[0];
                var data = msg.Buffer.Slice(1, msg.Buffer.Length);

                switch (udpHeader)
                {
                    case UdpHeader.ConnectRequest:
                        {
                            if (ProcessReliablePacket(data, msg.RemoteEndPoint))
                            {
                                var request = new Request(this, msg.RemoteEndPoint, Encoding.UTF8.GetString(data));
                                OnConnectionRequest?.Invoke(request);
                            }
                            continue;
                        }
                    case UdpHeader.ConnectionAccept:
                        {
                            if (ProcessReliablePacket(data, msg.RemoteEndPoint))
                            {
                                ConnectionInfos.Add(msg.RemoteEndPoint, new ConnectionInfo());
                                OnConnected?.Invoke(msg.RemoteEndPoint, null);
                            }
                            continue;
                        }
                    case UdpHeader.Acknowledgment:
                        {
                            uint sequenceNum = BitConverter.ToUInt32(data);
                            NotAcknowledgedPackets.Remove(new PacketInfo(msg.RemoteEndPoint, sequenceNum));
                            PacketAcknowledgedSignal.Set();
                            continue;
                        }
                    default:
                        {
                            if (ConnectionInfos.ContainsKey(msg.RemoteEndPoint))
                            {
                                switch (udpHeader)
                                {
                                    case UdpHeader.Unreliable:
                                        {
                                            OnDataReceive?.Invoke(data, msg.RemoteEndPoint, false);
                                            break;
                                        }
                                    case UdpHeader.Disconnection:
                                        {
                                            ProcessPeerDisconnection(msg.RemoteEndPoint, true);
                                            break;
                                        }
                                    case UdpHeader.Reliable:
                                        {
                                            if (ProcessReliablePacket(data, msg.RemoteEndPoint))
                                                OnDataReceive?.Invoke(data.Slice(sizeof(uint), data.Length), msg.RemoteEndPoint, true);
                                            break;
                                        }
                                }
                            }
                            break;
                        }
                };

                ConnectionInfo connectionInfo;

                if (ConnectionInfos.TryGetValue(msg.RemoteEndPoint, out connectionInfo))
                    connectionInfo.LastMessageTime = DateTime.UtcNow;
            }
        }

        private const int MaxRetransmissionAttempts = 6;
        private async Task<bool> SendReliableAsync(byte[] data, IPEndPoint endPoint, UdpHeader header)
        {
            var currentSequenceNum = Interlocked.Increment(ref SequenceNumber) - 1;
            var buffer = ArrayExtensions.CombineArrays([(byte)header], BitConverter.GetBytes(currentSequenceNum), data);
            var packetInfo = new PacketInfo(endPoint, currentSequenceNum);
            NotAcknowledgedPackets.Add(packetInfo);

            using var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            async Task WaitForPacketAcknowledgement()
            {
                while (NotAcknowledgedPackets.Contains(packetInfo))
                    await PacketAcknowledgedSignal.WaitOneAsync(token);
            }
            async Task RetransmitMessages()
            {
                int retransmissionDelay = 75;
                for (int attempts = 0; attempts < MaxRetransmissionAttempts; attempts++)
                {
                    if (token.IsCancellationRequested) return;

                    await _client.SendAsync(buffer, endPoint);
                    await Task.Delay(retransmissionDelay);
                    retransmissionDelay *= 2;
                }
            }

            var packetCheckTask = WaitForPacketAcknowledgement();
            var sendRetransmissionTask = RetransmitMessages();

            var completedTask = await Task.WhenAny(packetCheckTask, sendRetransmissionTask);
            tokenSource.Cancel();

            return completedTask == packetCheckTask;
        }
        private Task SendUnreliableAsync(byte[] data, IPEndPoint endPoint, UdpHeader header)
        {
            data = ArrayExtensions.CombineArrays([(byte)header], data);
            return _client.SendAsync(data, endPoint, cancellationToken).AsTask();
        }

        public Task SendUnreliableAsync(byte[] data)
        {
            if (hostEndPoint == null) return Task.CompletedTask;
            return SendUnreliableAsync(data, hostEndPoint);
        }
        public Task<bool> SendReliableAsync(byte[] data)
        {
            if (hostEndPoint == null) return Task.FromResult(false);
            return SendReliableAsync(data, hostEndPoint);
        }

        public Task SendUnreliableAsync(byte[] data, IPEndPoint endPoint) => SendUnreliableAsync(data, endPoint, UdpHeader.Unreliable);
        public Task<bool> SendReliableAsync(byte[] data, IPEndPoint endPoint) => SendReliableAsync(data, endPoint, UdpHeader.Reliable);

        public void SendReliable(byte[] data, IPEndPoint endPoint) => _ = SendReliableAsync(data, endPoint);
        public void SendReliable(byte[] data) => _ = SendReliableAsync(data);

        public void SendUnreliable(byte[] data, IPEndPoint endPoint) => _ = SendUnreliableAsync(data, endPoint);
        public void SendUnreliable(byte[] data) => _ = SendUnreliableAsync(data);


        private async Task SendReliableToAllAsync(byte[] data, UdpHeader udpHeader)
        {
            var sendTasks = new List<Task>(Connections.Count);

            foreach (var con in Connections)
                sendTasks.Add(SendReliableAsync(data, con, udpHeader));

            await Task.WhenAll(sendTasks);
        }
        private async Task SendUnreliableToAllAsync(byte[] data, UdpHeader header)
        {
            var sendTasks = new List<Task>(Connections.Count);

            foreach (var con in Connections)
                sendTasks.Add(SendUnreliableAsync(data, con, header));

            await Task.WhenAll(sendTasks);
        }

        public Task SendUnreliableToAllAsync(byte[] data) => SendUnreliableToAllAsync(data, UdpHeader.Unreliable);
        public void SendReliableToAll(byte[] data) => _ = SendReliableToAllAsync(data, UdpHeader.Reliable);
        public void SendUnreliableToAll(byte[] data) => _ = SendUnreliableToAllAsync(data);
        public Task SendReliableToAllAsync(byte[] data) => SendReliableToAllAsync(data, UdpHeader.Reliable);

        public void Dispose()
        {
            Stop();
            _client.Close();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _client.Close();
        }

        private enum UdpHeader
        {
            Reliable,
            Unreliable,
            Ping,
            ConnectRequest,
            ConnectionAccept,
            Disconnection,
            Acknowledgment
        }
    }
}

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
    /// <summary>
    /// Wraps a <see cref="WaitHandle"/> with a <see cref="Task{Boolean}"/>. If the <see cref="WaitHandle"/> is signalled, the returned task is (successfully) completed. If the observation is cancelled, the returned task is cancelled. If the handle is already signalled or the cancellation token is already cancelled, this method acts synchronously.
    /// </summary>
    /// <param name="handle">The <see cref="WaitHandle"/> to observe.</param>
    /// <param name="token">The cancellation token that cancels observing the <see cref="WaitHandle"/>.</param>
    public static Task WaitOneAsync(this WaitHandle handle, CancellationToken token)
    {
        return WaitOneAsync(handle, Timeout.InfiniteTimeSpan, token);
    }

    public static Task<bool> WaitOneAsync(this WaitHandle handle, int timeout, CancellationToken token)
    {
        return WaitOneAsync(handle, TimeSpan.FromMilliseconds(timeout), token);
    }

    /// <summary>
    /// Wraps a <see cref="WaitHandle"/> with a <see cref="Task{Boolean}"/>. If the <see cref="WaitHandle"/> is signalled, the returned task is completed with a <c>true</c> result. If the observation times out, the returned task is completed with a <c>false</c> result. If the observation is cancelled, the returned task is cancelled. If the handle is already signalled, the timeout is zero, or the cancellation token is already cancelled, then this method acts synchronously.
    /// </summary>
    /// <param name="handle">The <see cref="WaitHandle"/> to observe.</param>
    /// <param name="timeout">The timeout after which the <see cref="WaitHandle"/> is no longer observed.</param>
    /// <param name="token">The cancellation token that cancels observing the <see cref="WaitHandle"/>.</param>
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