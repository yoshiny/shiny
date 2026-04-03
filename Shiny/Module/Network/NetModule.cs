using Shiny.Core;
using Shiny.Feature;
using Shiny.Message;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public sealed class NetModule : IModule, IPostTickable {
        private ServerContext m_ServerContext = default!;
        private int m_NextServiceId = 0;
        private long m_NextConnectionId = 0;

        private readonly Dictionary<int, NetService> m_Services = new();
        private readonly Dictionary<long, TcpConnection> m_Connections = new();

        // 逻辑线程使用；确认只在逻辑线程访问后，也可换成 Queue<TcpConnection>
        private readonly ConcurrentQueue<TcpConnection> m_PendingFlushConnections = new();
        // 防止同一个连接反复入 flush 队列
        private readonly ConcurrentDictionary<long, byte> m_PendingFlushSet = new();


        public void OnInit(ServerContext context) {
            m_ServerContext = context;
        }

        public void OnStart() {
            
        }

        public void OnPostTick() {
            Flush();
        }

        public void OnStop() {
            foreach (var conn in m_Connections.Values.ToArray()) {
                conn.Close(NetCloseReason.ServiceStopped);
                conn.Dispose();
            }
            m_Connections.Clear();

            foreach (var service in m_Services.Values.ToArray()) {
                service.Dispose();
            }
            m_Services.Clear();

            while (m_PendingFlushConnections.TryDequeue(out _)) {
            }
            m_PendingFlushSet.Clear();
        }

        public int StartTcpListener(TcpListenOptions options) {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Protocol);
            ArgumentNullException.ThrowIfNull(options.MessageAdapter);

            int serviceId = Interlocked.Increment(ref m_NextServiceId);
            var service = new TcpListenService(this, serviceId, options);

            m_Services.Add(serviceId, service);
            service.Start();

            return serviceId;
        }

        public int StartTcpConnector(TcpConnectOptions options) {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Protocol);
            ArgumentNullException.ThrowIfNull(options.MessageAdapter);

            int serviceId = Interlocked.Increment(ref m_NextServiceId);
            var service = new TcpConnectService(this, serviceId, options);

            m_Services.Add(serviceId, service);
            service.Start();

            return serviceId;
        }

        public bool StopService(int serviceId) {
            if (!m_Services.Remove(serviceId, out var service)) {
                return false;
            }

            service.Dispose();

            var toClose = m_Connections.Values.Where(c => c.ServiceId == serviceId ).ToArray();
            foreach (var conn in toClose) {
                conn.Close(NetCloseReason.ServiceStopped);
            }

            return true;
        }

        public bool Send<T>(long connectionId, T message) {
            if (!m_Connections.TryGetValue(connectionId, out var conn)) {
                return false;
            }
            return conn.EnqueueSend(message);
        }

        public bool SendPacket(long connectionId, ReadOnlyMemory<byte> payload) {
            if (!m_Connections.TryGetValue(connectionId, out var conn)) {
                return false;
            }
            return conn.EnqueueRaw(payload);
        }

        public int Broadcast<T>(int serviceId, T message, Predicate<ConnectionInfo>? filter = null) {
            int count = 0;

            foreach (var conn in m_Connections.Values) {
                if (conn.ServiceId != serviceId) {
                    continue;
                }
                var info = conn.Info;
                if (filter != null && !filter(info)) {
                    continue;
                }
                if (conn.EnqueueSend(message)) {
                    count++;
                }
            }

            return count;
        }

        public bool Disconnect(long connectionId) {
            if (!m_Connections.TryGetValue(connectionId, out var conn))
                return false;

            conn.Close(NetCloseReason.LocalClosed);
            return true;
        }

        public bool TryGetConnectionInfo(long connectionId, out ConnectionInfo info) {
            if (m_Connections.TryGetValue(connectionId, out var conn)) {
                info = conn.Info;
                return true;
            }

            info = default;
            return false;
        }

        public IReadOnlyList<ConnectionInfo> GetConnectionsByService(int serviceId) {
            var list = new List<ConnectionInfo>();

            foreach (var conn in m_Connections.Values) {
                if (conn.ServiceId == serviceId) {
                    list.Add(conn.Info);
                }
            }

            return list;
        }

        public void Flush() {
            while (m_PendingFlushConnections.TryDequeue(out var conn)) {
                m_PendingFlushSet.TryRemove(conn.ConnectionId, out _);

                if (m_Connections.ContainsKey(conn.ConnectionId)) {
                    conn.TryScheduleSend();
                }
            }
        }

        internal void MarkConnectionPendingFlush(TcpConnection conn) {
            if (m_PendingFlushSet.TryAdd(conn.ConnectionId, 0)) {
                m_PendingFlushConnections.Enqueue(conn);
            }
        }

        internal void RegisterAcceptedConnection(NetService service, Socket socket, int receiveBufferSize) {
            long connId = Interlocked.Increment(ref m_NextConnectionId);
            var conn = new TcpConnection(this, service, connId, socket, receiveBufferSize);

            m_Connections.Add(connId, conn);

            var msg = service.MessageAdapter.CreateConnectedMessage(conn.Info);
            PostToServer(msg);

            conn.Start();
        }

        internal TcpConnection RegisterConnectedOutbound(NetService service, Socket socket, int receiveBufferSize) {
            long connId = Interlocked.Increment(ref m_NextConnectionId);
            var conn = new TcpConnection(this, service, connId, socket, receiveBufferSize);

            m_Connections.Add(connId, conn);

            var msg = service.MessageAdapter.CreateConnectedMessage(conn.Info);
            PostToServer(msg);

            return conn;
        }

        internal void OnConnectionClosed(TcpConnection conn, NetCloseReason reason) {
            if (m_Connections.Remove(conn.ConnectionId)) {
                var msg = conn.Info.ServiceId != 0
                    ? m_Services.TryGetValue(conn.ServiceId, out var service)
                        ? service.MessageAdapter.CreateDisconnectedMessage(conn.Info, reason)
                        : null
                    : null;

                conn.Dispose();

                if (msg != null) {
                    PostToServer(msg);
                }

                if (m_Services.TryGetValue(conn.ServiceId, out var netService) && netService is TcpConnectService connector) {
                    // 先简单处理：如果是出站服务断开，由 connector 自己重新 Start
                    // 若后续想避免重复启动，需要给 TcpConnectService 增加状态机
                    connector.Start();
                }
            }
        }

        internal void PostToServer(IServerMessage message) {
            m_ServerContext.PostMessage(message);
        }
    }
}
