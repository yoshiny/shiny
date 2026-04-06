using Shiny.Module.Network.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    internal sealed class TcpConnectService : NetService {
        private readonly TcpConnectOptions m_Options;
        private volatile bool m_Running;
        private int m_ConnectLoopActive;

        public TcpConnectService(NetModule owner, int serviceId, TcpConnectOptions options)
            : base(owner, serviceId, options.Name, NetServiceKind.Connector, options.Protocol, options.MessageAdapter) {
            m_Options = options;
        }

        public override void Start() {
            m_Running = true;
            TryStartConnectLoop();
        }

        public void RequestReconnect() {
            if (!m_Running || !m_Options.AutoReconnect) {
                return;
            }
            TryStartConnectLoop();
        }

        private void TryStartConnectLoop() {
            if (Interlocked.CompareExchange(ref m_ConnectLoopActive, 1, 0) != 0) {
                return;
            }
            _ = Task.Run(ConnectLoopAsync);
        }

        private async Task ConnectLoopAsync() {
            try {
                if (m_Options.AutoReconnect) {
                    await Task.Delay(m_Options.ReconnectDelayMs).ConfigureAwait(false);
                }
                while (m_Running) {
                    Socket? socket = null;
                    try {
                        var ip = IPAddress.Parse(m_Options.RemoteHost);
                        var ep = new IPEndPoint(ip, m_Options.RemotePort);

                        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) {
                            NoDelay = m_Options.NoDelay,
                            ReceiveBufferSize = m_Options.ReceiveBufferSize,
                            SendBufferSize = m_Options.SendBufferSize
                        };

                        if (m_Options.KeepAlive) {
                            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        }

                        await socket.ConnectAsync(ep).ConfigureAwait(false);

                        Owner.PostInternal(new NetConnectedInternalMessage(ServiceId, socket, m_Options.ReceiveBufferSize));

                        socket = null;
                        return;
                    } catch (Exception ex) {
                        socket?.Dispose();
                        Owner.PostInternal(new NetConnectFailedInternalMessage(ServiceId, ex));
                        if (!m_Options.AutoReconnect || !m_Running) {
                            return;
                        }
                        await Task.Delay(m_Options.ReconnectDelayMs).ConfigureAwait(false);
                    }
                }

            } finally {
                Volatile.Write(ref m_ConnectLoopActive, 0);
            }
        }

        public override void Dispose() {
            m_Running = false;
        }
    }
}
