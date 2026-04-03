using Shiny.Module.Network.Internal;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    internal sealed class TcpConnection : IDisposable {
        private readonly NetModule _owner;
        private readonly NetService _service;
        private readonly Socket _socket;

        private readonly INetFrameDecoder _decoder;
        private readonly INetFrameEncoder _encoder;

        private readonly Pipe _recvPipe;

        private readonly SocketAsyncEventArgs _recvArgs;
        private readonly SocketAsyncEventArgs _sendArgs;

        private readonly byte[] _recvBuffer;

        private readonly object _sendLock = new();
        private readonly Queue<OutgoingPacket> _sendQueue = new();

        private bool _sendScheduled;
        private bool _disposed;
        private bool _closedEventRaised;

        private byte[]? _currentSendBuffer;
        private IDisposable? _currentSendOwner;

        public long ConnectionId { get; }
        public int ServiceId => _service.ServiceId;
        public string ServiceName => _service.Name;

        public ConnectionInfo Info => new() {
            ConnectionId = ConnectionId,
            ServiceId = ServiceId,
            ServiceName = ServiceName,
            LocalEndPoint = SafeLocalEndPoint(),
            RemoteEndPoint = SafeRemoteEndPoint()
        };

        public TcpConnection(NetModule owner, NetService service, long connectionId, Socket socket, int receiveBufferSize) {
            _owner = owner;
            _service = service;
            _socket = socket;
            ConnectionId = connectionId;

            _decoder = service.Protocol.CreateDecoder();
            _encoder = service.Protocol.CreateEncoder();

            _recvPipe = new Pipe(new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.Inline,
                writerScheduler: PipeScheduler.Inline,
                pauseWriterThreshold: 64 * 1024,
                resumeWriterThreshold: 32 * 1024,
                minimumSegmentSize: 4096,
                useSynchronizationContext: false));

            _recvBuffer = ArrayPool<byte>.Shared.Rent(receiveBufferSize);

            _recvArgs = new SocketAsyncEventArgs();
            _sendArgs = new SocketAsyncEventArgs();

            _recvArgs.Completed += OnRecvCompleted;
            _sendArgs.Completed += OnSendCompleted;

            _recvArgs.SetBuffer(_recvBuffer, 0, _recvBuffer.Length);
        }

        public void Start() {
            StartReceive();
        }

        public bool EnqueueSend<T>(T message) {
            if (_disposed)
                return false;

            EncodedBuffer encoded;
            try {
                encoded = _encoder.Encode(message);
            } catch {
                return false;
            }

            if (!encoded.IsValid)
                return false;

            lock (_sendLock) {
                if (_disposed) {
                    encoded.Release();
                    return false;
                }

                _sendQueue.Enqueue(new OutgoingPacket(encoded));
            }

            _owner.MarkConnectionPendingFlush(this);
            return true;
        }

        public bool EnqueueRaw(ReadOnlyMemory<byte> payload) {
            if (_disposed)
                return false;

            var arr = payload.ToArray();
            var encoded = new EncodedBuffer(arr, 0, arr.Length);

            lock (_sendLock) {
                if (_disposed)
                    return false;

                _sendQueue.Enqueue(new OutgoingPacket(encoded));
            }

            _owner.MarkConnectionPendingFlush(this);
            return true;
        }

        public void TryScheduleSend() {
            if (_disposed)
                return;

            lock (_sendLock) {
                if (_disposed || _sendScheduled)
                    return;

                if (_sendQueue.Count == 0)
                    return;

                if (!BuildSendBuffer_NoLock(out var buffer, out var owner))
                    return;

                _sendScheduled = true;
                _currentSendBuffer = buffer;
                _currentSendOwner = owner;

                _sendArgs.SetBuffer(_currentSendBuffer, 0, _currentSendBuffer.Length);
            }

            try {
                bool pending = _socket.SendAsync(_sendArgs);
                if (!pending) {
                    ProcessSend(_sendArgs);
                }
            } catch (Exception) {
                Close(NetCloseReason.SendError);
            }
        }

        public void Close(NetCloseReason reason) {
            if (_disposed)
                return;

            DisposeSocketOnly();
            RaiseClosed(reason);
        }

        private void StartReceive() {
            if (_disposed)
                return;

            try {
                bool pending = _socket.ReceiveAsync(_recvArgs);
                if (!pending) {
                    ProcessReceive(_recvArgs);
                }
            } catch {
                Close(NetCloseReason.ReceiveError);
            }
        }

        private void OnRecvCompleted(object? sender, SocketAsyncEventArgs e) {
            ProcessReceive(e);
        }

        private void ProcessReceive(SocketAsyncEventArgs e) {
            if (_disposed)
                return;

            if (e.SocketError != SocketError.Success) {
                Close(NetCloseReason.ReceiveError);
                return;
            }

            if (e.BytesTransferred <= 0) {
                Close(NetCloseReason.RemoteClosed);
                return;
            }

            try {
                _recvPipe.Writer.Write(e.Buffer!.AsSpan(e.Offset, e.BytesTransferred));
                var flushResult = _recvPipe.Writer.FlushAsync().GetAwaiter().GetResult();

                DecodeAvailableFrames();

                if (flushResult.IsCompleted) {
                    Close(NetCloseReason.RemoteClosed);
                    return;
                }
            } catch {
                Close(NetCloseReason.ProtocolError);
                return;
            }

            StartReceive();
        }

        private void DecodeAvailableFrames() {
            while (true) {
                var result = _recvPipe.Reader.ReadAsync().GetAwaiter().GetResult();
                var buffer = result.Buffer;

                try {
                    while (_decoder.TryDecode(ref buffer, out var packet)) {
                        var msg = _service.MessageAdapter.CreateReceiveMessage(Info, packet);
                        _owner.PostToServer(msg);
                    }
                } catch {
                    _recvPipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                    throw;
                }

                _recvPipe.Reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || buffer.Length == 0)
                    break;
            }
        }

        private void OnSendCompleted(object? sender, SocketAsyncEventArgs e) {
            ProcessSend(e);
        }

        private void ProcessSend(SocketAsyncEventArgs e) {
            IDisposable? ownerToRelease = null;

            lock (_sendLock) {
                ownerToRelease = _currentSendOwner;
                _currentSendOwner = null;
                _currentSendBuffer = null;
                _sendScheduled = false;
            }

            ownerToRelease?.Dispose();

            if (_disposed)
                return;

            if (e.SocketError != SocketError.Success) {
                Close(NetCloseReason.SendError);
                return;
            }

            TryScheduleSend();
        }

        private bool BuildSendBuffer_NoLock(out byte[] buffer, out IDisposable? owner) {
            owner = null;
            buffer = Array.Empty<byte>();

            if (_sendQueue.Count == 0)
                return false;

            if (_sendQueue.Count == 1) {
                var packet = _sendQueue.Dequeue();
                var src = packet.Buffer;

                if (src.Array == null || src.Length <= 0) {
                    src.Release();
                    return false;
                }

                var result = ArrayPool<byte>.Shared.Rent(src.Length);
                Buffer.BlockCopy(src.Array, src.Offset, result, 0, src.Length);

                src.Release();

                buffer = result;
                owner = new Internal.ArrayPoolBufferOwner(result);
                return true;
            }

            int total = 0;
            foreach (var packet in _sendQueue) {
                total += packet.Buffer.Length;
            }

            var merged = ArrayPool<byte>.Shared.Rent(total);
            int writeOffset = 0;

            while (_sendQueue.Count > 0) {
                var packet = _sendQueue.Dequeue();
                var src = packet.Buffer;

                if (src.Array != null && src.Length > 0) {
                    Buffer.BlockCopy(src.Array, src.Offset, merged, writeOffset, src.Length);
                    writeOffset += src.Length;
                }

                src.Release();
            }

            if (writeOffset == 0) {
                ArrayPool<byte>.Shared.Return(merged);
                return false;
            }

            if (writeOffset != total) {
                var exact = ArrayPool<byte>.Shared.Rent(writeOffset);
                Buffer.BlockCopy(merged, 0, exact, 0, writeOffset);
                ArrayPool<byte>.Shared.Return(merged);

                buffer = exact[..writeOffset];
                owner = new Internal.ArrayPoolBufferOwner(exact);
                return true;
            }

            buffer = merged[..writeOffset];
            owner = new Internal.ArrayPoolBufferOwner(merged);
            return true;
        }

        private EndPoint? SafeLocalEndPoint() {
            try { return _socket.LocalEndPoint; } catch { return null; }
        }

        private EndPoint? SafeRemoteEndPoint() {
            try { return _socket.RemoteEndPoint; } catch { return null; }
        }

        private void DisposeSocketOnly() {
            if (_disposed)
                return;

            _disposed = true;

            try { _socket.Shutdown(SocketShutdown.Both); } catch { }
            try { _socket.Close(); } catch { }
        }

        private void RaiseClosed(NetCloseReason reason) {
            if (_closedEventRaised)
                return;

            _closedEventRaised = true;
            _owner.OnConnectionClosed(this, reason);
        }

        public void Dispose() {
            DisposeSocketOnly();

            try {
                _recvArgs.Dispose();
                _sendArgs.Dispose();
            } catch {
            }

            try {
                _recvPipe.Reader.Complete();
                _recvPipe.Writer.Complete();
            } catch {
            }

            lock (_sendLock) {
                while (_sendQueue.Count > 0) {
                    var packet = _sendQueue.Dequeue();
                    packet.Buffer.Release();
                }

                _currentSendOwner?.Dispose();
                _currentSendOwner = null;
                _currentSendBuffer = null;
            }

            ArrayPool<byte>.Shared.Return(_recvBuffer);
        }
    }
}
