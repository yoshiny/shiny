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
        private readonly NetModule m_Owner;
        private readonly NetService m_Service;
        private readonly Socket m_Socket;

        private readonly INetFrameDecoder m_Decoder;
        private readonly INetFrameEncoder m_Encoder;

        private readonly Pipe m_RecvPipe;

        private readonly SocketAsyncEventArgs m_RecvArgs;
        private readonly SocketAsyncEventArgs m_SendArgs;

        private readonly byte[] m_RecvBuffer;

        private readonly object m_SendLock = new();
        private readonly Queue<OutgoingPacket> m_SendQueue = new();

        private bool m_SendScheduled;
        private bool m_Disposed;
        private bool m_ClosedEventRaised;

        private byte[]? m_CurrentSendBuffer;
        private IDisposable? m_CurrentSendOwner;

        public long ConnectionId { get; }
        public int ServiceId => m_Service.ServiceId;
        public string ServiceName => m_Service.Name;

        public ConnectionInfo Info => new() {
            ConnectionId = ConnectionId,
            ServiceId = ServiceId,
            ServiceName = ServiceName,
            LocalEndPoint = SafeLocalEndPoint(),
            RemoteEndPoint = SafeRemoteEndPoint()
        };

        public TcpConnection(NetModule owner, NetService service, long connectionId, Socket socket, int receiveBufferSize) {
            m_Owner = owner;
            m_Service = service;
            m_Socket = socket;
            ConnectionId = connectionId;

            m_Decoder = service.Protocol.CreateDecoder();
            m_Encoder = service.Protocol.CreateEncoder();

            m_RecvPipe = new Pipe(new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.Inline,
                writerScheduler: PipeScheduler.Inline,
                pauseWriterThreshold: 64 * 1024,
                resumeWriterThreshold: 32 * 1024,
                minimumSegmentSize: 4096,
                useSynchronizationContext: false));

            m_RecvBuffer = ArrayPool<byte>.Shared.Rent(receiveBufferSize);

            m_RecvArgs = new SocketAsyncEventArgs();
            m_SendArgs = new SocketAsyncEventArgs();

            m_RecvArgs.Completed += OnRecvCompleted;
            m_SendArgs.Completed += OnSendCompleted;

            m_RecvArgs.SetBuffer(m_RecvBuffer, 0, m_RecvBuffer.Length);
        }

        public void Start() {
            StartReceive();
        }

        public bool EnqueueSend<T>(T message) {
            if (m_Disposed) {
                return false;
            }

            EncodedBuffer encoded;
            try {
                encoded = m_Encoder.Encode(message);
            } catch {
                return false;
            }

            if (!encoded.IsValid) {
                return false;
            }

            lock (m_SendLock) {
                if (m_Disposed) {
                    encoded.Release();
                    return false;
                }
                m_SendQueue.Enqueue(new OutgoingPacket(encoded));
            }

            m_Owner.MarkConnectionPendingFlush(this);
            return true;
        }

        public bool EnqueueRaw(ReadOnlyMemory<byte> payload) {
            if (m_Disposed) {
                return false;
            }

            var arr = payload.ToArray();
            var encoded = new EncodedBuffer(arr, 0, arr.Length);

            lock (m_SendLock) {
                if (m_Disposed) {
                    return false;
                }
                m_SendQueue.Enqueue(new OutgoingPacket(encoded));
            }

            m_Owner.MarkConnectionPendingFlush(this);
            return true;
        }

        public void TryScheduleSend() {
            if (m_Disposed) {
                return;
            }

            lock (m_SendLock) {
                if (m_Disposed || m_SendScheduled) {
                    return;
                }

                if (m_SendQueue.Count == 0) {
                    return;
                }

                if (!BuildSendBuffer_NoLock(out var buffer, out var owner)) {
                    return;
                }

                m_SendScheduled = true;
                m_CurrentSendBuffer = buffer;
                m_CurrentSendOwner = owner;

                m_SendArgs.SetBuffer(m_CurrentSendBuffer, 0, m_CurrentSendBuffer.Length);
            }

            try {
                bool pending = m_Socket.SendAsync(m_SendArgs);
                if (!pending) {
                    ProcessSend(m_SendArgs);
                }
            } catch (Exception) {
                Close(NetCloseReason.SendError);
            }
        }

        public void Close(NetCloseReason reason) {
            if (m_Disposed) {
                return;
            }

            DisposeSocketOnly();
            RaiseClosed(reason);
        }

        private void StartReceive() {
            if (m_Disposed) {
                return;
            }

            try {
                bool pending = m_Socket.ReceiveAsync(m_RecvArgs);
                if (!pending) {
                    ProcessReceive(m_RecvArgs);
                }
            } catch {
                Close(NetCloseReason.ReceiveError);
            }
        }

        private void OnRecvCompleted(object? sender, SocketAsyncEventArgs e) {
            ProcessReceive(e);
        }

        private void ProcessReceive(SocketAsyncEventArgs e) {
            if (m_Disposed) {
                return;
            }

            if (e.SocketError != SocketError.Success) {
                Close(NetCloseReason.ReceiveError);
                return;
            }

            if (e.BytesTransferred <= 0) {
                Close(NetCloseReason.RemoteClosed);
                return;
            }

            try {
                m_RecvPipe.Writer.Write(e.Buffer!.AsSpan(e.Offset, e.BytesTransferred));
                var flushResult = m_RecvPipe.Writer.FlushAsync().GetAwaiter().GetResult();

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
                var result = m_RecvPipe.Reader.ReadAsync().GetAwaiter().GetResult();
                var buffer = result.Buffer;

                try {
                    while (m_Decoder.TryDecode(ref buffer, out var packet)) {
                        var msg = m_Service.MessageAdapter.CreateReceiveMessage(Info, packet);
                        m_Owner.PostInternal(new NetReceivedInternalMessage(ConnectionId, packet));
                    }
                } catch {
                    m_RecvPipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                    throw;
                }

                m_RecvPipe.Reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || buffer.Length == 0) {
                    break;
                }
            }
        }

        private void OnSendCompleted(object? sender, SocketAsyncEventArgs e) {
            ProcessSend(e);
        }

        private void ProcessSend(SocketAsyncEventArgs e) {
            IDisposable? ownerToRelease = null;

            lock (m_SendLock) {
                ownerToRelease = m_CurrentSendOwner;
                m_CurrentSendOwner = null;
                m_CurrentSendBuffer = null;
                m_SendScheduled = false;
            }

            ownerToRelease?.Dispose();

            if (m_Disposed) {
                return;
            }

            if (e.SocketError != SocketError.Success) {
                Close(NetCloseReason.SendError);
                return;
            }

            TryScheduleSend();
        }

        private bool BuildSendBuffer_NoLock(out byte[] buffer, out IDisposable? owner) {
            owner = null;
            buffer = Array.Empty<byte>();

            if (m_SendQueue.Count == 0) {
                return false;
            }

            if (m_SendQueue.Count == 1) {
                var packet = m_SendQueue.Dequeue();
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
            foreach (var packet in m_SendQueue) {
                total += packet.Buffer.Length;
            }

            var merged = ArrayPool<byte>.Shared.Rent(total);
            int writeOffset = 0;

            while (m_SendQueue.Count > 0) {
                var packet = m_SendQueue.Dequeue();
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
            try { return m_Socket.LocalEndPoint; } catch { return null; }
        }

        private EndPoint? SafeRemoteEndPoint() {
            try { return m_Socket.RemoteEndPoint; } catch { return null; }
        }

        private void DisposeSocketOnly() {
            if (m_Disposed) {
                return;
            }

            m_Disposed = true;

            try { m_Socket.Shutdown(SocketShutdown.Both); } catch { }
            try { m_Socket.Close(); } catch { }
        }

        private void RaiseClosed(NetCloseReason reason) {
            if (m_ClosedEventRaised)
                return;

            m_ClosedEventRaised = true;
            m_Owner.PostInternal(new NetClosedInternalMessage(ConnectionId, reason));
        }

        public void Dispose() {
            DisposeSocketOnly();

            try {
                m_RecvArgs.Dispose();
                m_SendArgs.Dispose();
            } catch {
            }

            try {
                m_RecvPipe.Reader.Complete();
                m_RecvPipe.Writer.Complete();
            } catch {
            }

            lock (m_SendLock) {
                while (m_SendQueue.Count > 0) {
                    var packet = m_SendQueue.Dequeue();
                    packet.Buffer.Release();
                }

                m_CurrentSendOwner?.Dispose();
                m_CurrentSendOwner = null;
                m_CurrentSendBuffer = null;
            }

            ArrayPool<byte>.Shared.Return(m_RecvBuffer);
        }
    }
}
