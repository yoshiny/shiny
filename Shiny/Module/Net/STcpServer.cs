using Serilog;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Net {
    public sealed class STcpServer : IDisposable {
        private readonly Socket m_ListenSocket;
        private readonly SocketAsyncEventArgs m_AcceptArgs;
        private readonly ConcurrentDictionary<int, STcpConnection> m_Connectionds;
        private readonly PipeOptions m_PipeOptions;
        private readonly ITcpPacketDispatcher m_Dispatcher;
        private readonly int m_RecvBufferSize;
        private readonly int m_ListenBacklog;

        private int m_NextConnectionId;
        private volatile bool m_Running;

        public STcpServer(IPEndPoint endPoint, ITcpPacketDispatcher dispatcher, int recvBufferSize = 8 * 1024, int listenBacklog = 512 ) {
            m_RecvBufferSize = recvBufferSize;
            m_ListenBacklog = listenBacklog;
            m_Dispatcher = dispatcher;

            m_ListenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            m_ListenSocket.NoDelay = true;
            m_ListenSocket.Bind(endPoint);

            m_AcceptArgs = new SocketAsyncEventArgs();
            m_AcceptArgs.Completed += OnIoCompleted;

            m_PipeOptions = new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.Inline,
                writerScheduler: PipeScheduler.Inline,
                pauseWriterThreshold: 64 * 1024,
                resumeWriterThreshold: 32 * 1024,
                minimumSegmentSize: 4 * 1024,
                useSynchronizationContext: false
                );

            m_Connectionds = new ConcurrentDictionary<int, STcpConnection>();
        }

        public void Start() {
            if (m_Running) {
                return;
            }
            m_ListenSocket.Listen(m_ListenBacklog);
            m_Running = true;
            PostAccept();
        }

        public void Stop() {
            if (!m_Running) {
                return;
            }
            m_Running = false;
            try {
                m_ListenSocket.Close();
            } catch (Exception ex) {
                Log.Error("listen socket close failed, {Exception}", ex);
            }
            foreach (var iter in m_Connectionds) {
                CloseConnection(iter.Value);
            }
        }

        public void Send(int connection_id, byte[] buffer, int offset, int length, bool returnBufferToPool) {
            if (!m_Connectionds.TryGetValue(connection_id, out var connection)) {
                if (returnBufferToPool) {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                return;
            }
            connection.EnqueueSend(buffer, offset, length, returnBufferToPool);
        }

        public void Dispose() {
            Stop();
            m_AcceptArgs.Dispose();
            m_ListenSocket.Dispose();
        }

        private void PostAccept() {
            if (!m_Running) {
                return;
            }

            m_AcceptArgs.AcceptSocket = null;
            bool pending = m_ListenSocket.AcceptAsync(m_AcceptArgs);
            if (!pending) {
                ProcessAccept(m_AcceptArgs);
            }
        }

        private void OnIoCompleted(object? sender, SocketAsyncEventArgs e) {
            switch (e.LastOperation) {
                case SocketAsyncOperation.Accept:
                    ProcessAccept(e);
                    break;
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new NotSupportedException($"Unexpected socket op: {e.LastOperation}");
            }
        }

        private void ProcessAccept(SocketAsyncEventArgs e) {
            try {
                if (!m_Running) {
                    return;
                }
                if (e.SocketError != SocketError.Success || e.AcceptSocket is null) {
                    return;
                }

                Socket socket = e.AcceptSocket;
                socket.NoDelay = true;

                int connection_id = Interlocked.Increment(ref m_NextConnectionId);
                var connection = new STcpConnection(this, connection_id, socket, m_RecvBufferSize, m_PipeOptions);
                connection.RecvArgs.Completed += OnIoCompleted;
                connection.SendArgs.Completed += OnIoCompleted;

                if (!m_Connectionds.TryAdd(connection_id, connection)) {
                    connection.Dispose();
                    return;
                }

                m_Dispatcher.OnConnected(connection);
                PostReceive(connection);
            } catch (Exception ex) {
                Log.Error("STcpServer.ProcessAccept failed, {Exception}", ex);
            } finally {
                if (m_Running) {
                    PostAccept();
                }
            }
        }

        private void PostReceive(STcpConnection connection) {
            if (Volatile.Read(ref connection.IsClosed) != 0) {
                return;
            }

            bool pending = connection.Socket.ReceiveAsync(connection.RecvArgs);
            if (!pending) {
                ProcessReceive(connection.RecvArgs);
            }
        }

        private void ProcessReceive(SocketAsyncEventArgs e) {
            var connection = (STcpConnection)e.UserToken!;
            if (Volatile.Read(ref connection.IsClosed) != 0) {
                return;
            }

            if (e.SocketError != SocketError.Success || e.BytesTransferred <= 0) {
                CloseConnection(connection);
                return;
            }

            try {
                WriteReceivedBytesToPipe(connection, e.Buffer!, e.Offset, e.BytesTransferred);
                ParseAvailableFrames(connection);
                PostReceive(connection);
            } catch (Exception ex) {
                Log.Error("STcpServer.ProcessReceive error, {ConnectionId}, {Exception}", connection.ConnectionId, ex);
                CloseConnection(connection);
            }
        }

        private static void WriteReceivedBytesToPipe(STcpConnection connection, byte[] source, int offset, int count) {
            PipeWriter writer = connection.InputPipe.Writer;
            Memory<byte> memory = writer.GetMemory(count);
            source.AsSpan(offset, count).CopyTo(memory.Span);
            writer.Advance(count);

            FlushResult flush = writer.FlushAsync().GetAwaiter().GetResult();
            if (flush.IsCanceled || flush.IsCompleted) {
                throw new InvalidOperationException("Pipe writer completed unexpectedly.");
            }
        }

        private void ParseAvailableFrames(STcpConnection connection) {
            PipeReader reader = connection.InputPipe.Reader;
            ReadResult result = reader.ReadAsync().GetAwaiter().GetResult();
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try {
                while (TryDispatchOneFrame(connection, ref buffer)) {
                }
                consumed = buffer.Start;
                // 如果当前剩下的是半包，说明这批数据已经全部检查过了，
                // 但还不能消费掉，所以 examined 要保留在 End，避免 ReadAsync 立即重复返回同一份数据。
                // 如果已经全部吃完，则 examined 跟 consumed 对齐即可。
                examined = buffer.Length == 0 ? consumed : result.Buffer.End;
            } finally {
                reader.AdvanceTo(consumed, examined);
            }
        }

        private bool TryDispatchOneFrame(STcpConnection connection, ref ReadOnlySequence<byte> buffer) {
            if (!SLimitedLengthCodec.TryReadFrame(ref buffer, out ReadOnlySequence<byte> payload)) {
                return false;
            }

            int length = checked((int)payload.Length);
            SPacketLease lease = SPacketPool.Shared.Rent(length);
            payload.CopyTo(lease.Memory.Span);
            m_Dispatcher.Dispatch(connection, lease);

            return true;
        }

        private void ProcessSend(SocketAsyncEventArgs e) {
            var connection = (STcpConnection)e.UserToken!;
            if (Volatile.Read(ref connection.IsClosed) != 0) {
                return;
            }
            if (e.SocketError != SocketError.Success || e.BytesTransferred <= 0) {
                CloseConnection(connection);
                return;
            }
            if (!connection.SendQueue.TryPeek(out var packet)) {
                Interlocked.Exchange(ref connection.IsSending, 0);
                return;
            }
            packet.Sent += e.BytesTransferred;
            if (packet.Sent >= packet.Length) {
                connection.SendQueue.TryDequeue(out var completed);
                completed?.Dispose();
            }
            StartSend(connection);
        }

        internal void StartSend(STcpConnection connection) {
            if (Volatile.Read(ref connection.IsClosed) != 0) {
                return;
            }
            if (!connection.SendQueue.TryPeek(out var packet)) {
                Interlocked.Exchange(ref connection.IsSending, 0);

                if (!connection.SendQueue.IsEmpty && Interlocked.CompareExchange(ref connection.IsSending, 1, 0) == 0) {
                    StartSend(connection);
                }

                return;
            }

            connection.SendArgs.SetBuffer(packet.Buffer, packet.Offset + packet.Sent, packet.Length - packet.Sent);
            bool pending = connection.Socket.SendAsync(connection.SendArgs);
            if (!pending) {
                ProcessSend(connection.SendArgs);
            }
        }

        private void CloseConnection(STcpConnection connection) {
            if (Interlocked.Exchange(ref connection.IsClosed, 1) != 0) {
                return;
            }
            m_Connectionds.TryRemove(connection.ConnectionId, out _);
            try {
                connection.Socket.Shutdown(SocketShutdown.Both);
            } catch (Exception ex) {
                Log.Error("STcpServer.CloseConnection error, {ConnectionId}, {Exception}", connection.ConnectionId, ex);
            }

            try {
                m_Dispatcher.OnDisconnected(connection);
            } catch {
            }

            connection.Dispose();
        }
    }
}
