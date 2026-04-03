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
    public class STcpConnection : IDisposable {
        public STcpServer TcpServer { get; }
        public int ConnectionId { get; }
        public Socket Socket { get; }
        public EndPoint? RemoteEndPoint => Socket.RemoteEndPoint;
        public SocketAsyncEventArgs RecvArgs { get; }
        public SocketAsyncEventArgs SendArgs { get; }
        public Pipe InputPipe { get; }
        public byte[] RecvBuffer { get; }
        public ConcurrentQueue<SNetPacket> SendQueue { get; }

        public int IsSending;
        public int IsClosed;

        public STcpConnection(STcpServer tcpServer, int connectionId, Socket socket, int recvBufferSize, PipeOptions pipeOptions) {
            TcpServer = tcpServer;
            ConnectionId = connectionId;
            Socket = socket;
            InputPipe = new Pipe(pipeOptions);

            RecvBuffer = ArrayPool<byte>.Shared.Rent(recvBufferSize);

            RecvArgs = new SocketAsyncEventArgs { UserToken = this };
            RecvArgs.SetBuffer(RecvBuffer, 0, recvBufferSize);

            SendArgs = new SocketAsyncEventArgs { UserToken = this };
            SendQueue = new ConcurrentQueue<SNetPacket>();
        }

        public void EnqueueSend(byte[] buffer, int offset, int length, bool returnBufferToPool) {
            if (Volatile.Read(ref IsClosed) != 0) {
                if (returnBufferToPool) {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                return;
            }

            SendQueue.Enqueue(new SNetPacket(buffer, offset, length, returnBufferToPool));

            if (Interlocked.CompareExchange(ref IsSending, 1, 0) == 0) {
                TcpServer.StartSend(this);
            }
        }

        public void Dispose() {
            try {
                Socket.Dispose();
            } catch {
                // ignored
            }

            while (SendQueue.TryDequeue(out var packet)) {
                packet.Dispose();
            }

            try {
                InputPipe.Reader.Complete();
            } catch {
                // ignored
            }

            try {
                InputPipe.Writer.Complete();
            } catch {
                // ignored
            }

            RecvArgs.Dispose();
            SendArgs.Dispose();
            ArrayPool<byte>.Shared.Return(RecvBuffer);
        }
    }
}
