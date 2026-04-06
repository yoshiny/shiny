using Shiny.Core;
using Shiny.Feature;
using Shiny.Message;
using Shiny.Module.Network.Internal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
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
        private readonly Dictionary<int, int> m_ServiceConnectionCounts = new();

        private readonly Queue<TcpConnection> m_PendingFlushConnections = new();
        private readonly HashSet<long> m_PendingFlushSet = new();


        public void OnInit(ServerContext context) {
            m_ServerContext = context;

            context.Dispatcher.Register<NetAcceptedInternalMessage>(OnAcceptedInternal);
            context.Dispatcher.Register<NetConnectedInternalMessage>(OnConnectedInternal);
            context.Dispatcher.Register<NetConnectFailedInternalMessage>(OnConnectFailedInternal);
            context.Dispatcher.Register<NetReceivedInternalMessage>(OnReceivedInternal);
            context.Dispatcher.Register<NetClosedInternalMessage>(OnClosedInternal);
        }

        public void OnStart() {
            
        }

        public void OnPostTick() {
            m_ServerContext.VerifyAccess();
            Flush();
        }

        public void OnStop() {
            m_ServerContext.VerifyAccess();

            foreach (var conn in m_Connections.Values.ToArray()) {
                conn.Dispose();
            }
            m_Connections.Clear();

            foreach (var service in m_Services.Values.ToArray()) {
                service.Dispose();
            }
            m_Services.Clear();
            m_ServiceConnectionCounts.Clear();
            m_PendingFlushConnections.Clear();
            m_PendingFlushSet.Clear();
        }

        public int StartTcpListener(TcpListenOptions options) {
            m_ServerContext.VerifyAccess();

            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Protocol);
            ArgumentNullException.ThrowIfNull(options.MessageAdapter);

            int serviceId = Interlocked.Increment(ref m_NextServiceId);
            var service = new TcpListenService(this, serviceId, options);

            m_Services.Add(serviceId, service);
            m_ServiceConnectionCounts.Add(serviceId, 0);
            service.Start();

            return serviceId;
        }

        public int StartTcpConnector(TcpConnectOptions options) {
            m_ServerContext.VerifyAccess();

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
            m_ServerContext.VerifyAccess();

            if (!m_Services.Remove(serviceId, out var service)) {
                return false;
            }

            if (service is TcpListenService) {
                m_ServiceConnectionCounts.Remove(serviceId);
            }

            service.Dispose();

            var toClose = m_Connections.Values.Where(c => c.ServiceId == serviceId ).ToArray();
            foreach (var conn in toClose) {
                conn.Close(NetCloseReason.ServiceStopped);
            }

            return true;
        }

        public bool Send<T>(long connectionId, T message) {
            m_ServerContext.VerifyAccess();

            if (!m_Connections.TryGetValue(connectionId, out var conn)) {
                return false;
            }

            return conn.EnqueueSend(message);
        }

        public bool SendPacket(long connectionId, ReadOnlyMemory<byte> payload) {
            m_ServerContext.VerifyAccess();

            if (!m_Connections.TryGetValue(connectionId, out var conn)) {
                return false;
            }

            return conn.EnqueueRaw(payload);
        }

        public int Broadcast<T>(int serviceId, T message, Predicate<ConnectionInfo>? filter = null) {
            m_ServerContext.VerifyAccess();

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
            m_ServerContext.VerifyAccess();

            if (!m_Connections.TryGetValue(connectionId, out var conn)) {
                return false;
            }

            conn.Close(NetCloseReason.LocalClosed);
            return true;
        }

        public bool TryGetConnectionInfo(long connectionId, out ConnectionInfo info) {
            m_ServerContext.VerifyAccess();

            if (m_Connections.TryGetValue(connectionId, out var conn)) {
                info = conn.Info;
                return true;
            }

            info = default;
            return false;
        }

        public IReadOnlyList<ConnectionInfo> GetConnectionsByService(int serviceId) {
            m_ServerContext.VerifyAccess();

            var list = new List<ConnectionInfo>();
            foreach (var conn in m_Connections.Values) {
                if (conn.ServiceId == serviceId) {
                    list.Add(conn.Info);
                }
            }

            return list;
        }

        public int GetServiceConnectionCount(int serviceId) {
            m_ServerContext.VerifyAccess();
            return m_ServiceConnectionCounts.TryGetValue(serviceId, out var count) ? count : 0;
        }

        internal void MarkConnectionPendingFlush(TcpConnection conn) {
            m_ServerContext.VerifyAccess();

            if (m_PendingFlushSet.Add(conn.ConnectionId)) {
                m_PendingFlushConnections.Enqueue(conn);
            }
        }

        internal void PostInternal(IServerMessage message) {
            m_ServerContext.PostMessage(message);
        }

        private void Flush() {
            m_ServerContext.VerifyAccess();

            while (m_PendingFlushConnections.Count > 0) {
                var conn = m_PendingFlushConnections.Dequeue();
                m_PendingFlushSet.Remove(conn.ConnectionId);
                if (m_Connections.ContainsKey(conn.ConnectionId)) {
                    conn.TryScheduleSend();
                }
            }
        }

        private void OnAcceptedInternal(NetAcceptedInternalMessage msg) {
            m_ServerContext.VerifyAccess();

            if (!m_Services.TryGetValue(msg.ServiceId, out var service)) {
                msg.Socket.Dispose();
                return;
            }

            if (service is not TcpListenService listenService) {
                msg.Socket.Dispose();
                return;
            }

            if (!m_ServiceConnectionCounts.TryGetValue(msg.ServiceId, out int currentCount)) {
                msg.Socket.Dispose();
                return;
            }

            if (currentCount >= listenService.Options.MaxConnections) {
                msg.Socket.Dispose();
                return;
            }

            long connId = Interlocked.Increment(ref m_NextConnectionId);
            var conn = new TcpConnection(this, service, connId, msg.Socket, msg.ReceiveBufferSize);
            m_Connections.Add(connId, conn);
            m_ServiceConnectionCounts[msg.ServiceId] = currentCount + 1;

            var connectedMessage = service.MessageAdapter.CreateConnectedMessage(conn.Info);
            m_ServerContext.PostMessage(connectedMessage);

            conn.Start();
        }

        private void OnConnectedInternal(NetConnectedInternalMessage msg) {
            m_ServerContext.VerifyAccess();

            if (!m_Services.TryGetValue(msg.ServiceId, out var service)) {
                msg.Socket.Dispose();
                return;
            }

            long connId = Interlocked.Increment(ref m_NextConnectionId);
            var conn = new TcpConnection(this, service, connId, msg.Socket, msg.ReceiveBufferSize);
            m_Connections.Add(connId, conn);

            var connectedMessage = service.MessageAdapter.CreateConnectedMessage(conn.Info);
            m_ServerContext.PostMessage(connectedMessage);

            conn.Start();
        }

        private void OnConnectFailedInternal(NetConnectFailedInternalMessage msg) {
            m_ServerContext.VerifyAccess();

            if (!m_Services.TryGetValue(msg.ServiceId, out var service)) {
                return;
            }

            var businssMessage = service.MessageAdapter.CreateConnectFailedMessage(service.ServiceId, service.Name, msg.Exception);
            m_ServerContext.PostMessage(businssMessage);

            if (service is TcpConnectService connector) {
                connector.RequestReconnect();
            }
        }

        private void OnReceivedInternal(NetReceivedInternalMessage msg) {
            m_ServerContext.VerifyAccess();

            if (!m_Connections.TryGetValue(msg.ConnectionId, out var conn)) {
                return;
            }

            if (!m_Services.TryGetValue(conn.ServiceId, out var service)) {
                return;
            }

            var businessMessage = service.MessageAdapter.CreateReceiveMessage(conn.Info, msg.Packet);
            m_ServerContext.PostMessage(businessMessage);
        }

        private void OnClosedInternal(NetClosedInternalMessage msg) {
            m_ServerContext.VerifyAccess();

            if (!m_Connections.Remove(msg.ConnectionId, out var conn)) {
                return;
            }

            m_PendingFlushSet.Remove(msg.ConnectionId);

            var info = conn.Info;
            conn.Dispose();

            if (m_Services.TryGetValue(info.ServiceId, out var service)) {
                if (service is TcpListenService) {
                    if (m_ServiceConnectionCounts.TryGetValue(info.ServiceId, out int count) && count > 0) {
                        m_ServiceConnectionCounts[info.ServiceId] = count - 1;
                    }
                }

                var businessMessage = service.MessageAdapter.CreateDisconnectedMessage(info, msg.Reason);
                m_ServerContext.PostMessage(businessMessage);

                if (service is TcpConnectService connector) {
                    connector.RequestReconnect();
                }
            }
        }

    }
}
