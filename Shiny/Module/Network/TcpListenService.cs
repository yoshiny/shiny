using Shiny.Module.Network.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    internal sealed class TcpListenService : NetService {
        private readonly TcpListenOptions m_Options;
        private Socket? m_ListenSocket;
        private volatile bool m_Running;

        public TcpListenService(NetModule owner, int serviceId, TcpListenOptions options)
            : base(owner, serviceId, options.Name, NetServiceKind.Listener, options.Protocol, options.MessageAdapter) {
            m_Options = options;
        }

        public override void Start() {
            if (m_Running)
                return;

            IPAddress ip = IPAddress.Parse(m_Options.Host);
            var ep = new IPEndPoint(ip, m_Options.Port);

            m_ListenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            m_ListenSocket.Bind(ep);
            m_ListenSocket.Listen(m_Options.Backlog);

            m_Running = true;

            for (int i = 0; i < Math.Max(1, m_Options.AcceptSocketCount); i++) {
                _ = Task.Run(AcceptLoopAsync);
            }
        }

        private async Task AcceptLoopAsync() {
            while (m_Running && m_ListenSocket != null) {
                Socket? client = null;
                try {
                    client = await m_ListenSocket.AcceptAsync().ConfigureAwait(false);

                    client.NoDelay = m_Options.NoDelay;
                    if (m_Options.KeepAlive) {
                        client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    }

                    client.ReceiveBufferSize = m_Options.ReceiveBufferSize;
                    client.SendBufferSize = m_Options.SendBufferSize;

                    Owner.PostInternal( new NetAcceptedInternalMessage(ServiceId, client, m_Options.ReceiveBufferSize));
                } catch (ObjectDisposedException) {
                    break;
                } catch {
                    client?.Dispose();
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
        }

        public override void Dispose() {
            m_Running = false;

            try { m_ListenSocket?.Close(); } catch { }
            try { m_ListenSocket?.Dispose(); } catch { }

            m_ListenSocket = null;
        }
    }
}
