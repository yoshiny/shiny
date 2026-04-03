using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    internal sealed class TcpListenService : NetService {
        private readonly TcpListenOptions _options;
        private Socket? _listenSocket;
        private volatile bool _running;

        public TcpListenService(NetModule owner, int serviceId, TcpListenOptions options)
            : base(owner, serviceId, options.Name, NetServiceKind.Listener, options.Protocol, options.MessageAdapter) {
            _options = options;
        }

        public override void Start() {
            if (_running)
                return;

            IPAddress ip = IPAddress.Parse(_options.Host);
            var ep = new IPEndPoint(ip, _options.Port);

            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(ep);
            _listenSocket.Listen(_options.Backlog);

            _running = true;

            for (int i = 0; i < Math.Max(1, _options.AcceptSocketCount); i++) {
                _ = Task.Run(AcceptLoopAsync);
            }
        }

        private async Task AcceptLoopAsync() {
            while (_running && _listenSocket != null) {
                Socket? client = null;
                try {
                    client = await _listenSocket.AcceptAsync();

                    client.NoDelay = _options.NoDelay;
                    if (_options.KeepAlive) {
                        client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    }

                    client.ReceiveBufferSize = _options.ReceiveBufferSize;
                    client.SendBufferSize = _options.SendBufferSize;

                    Owner.RegisterAcceptedConnection(this, client, _options.ReceiveBufferSize);
                } catch (ObjectDisposedException) {
                    break;
                } catch {
                    client?.Dispose();
                    await Task.Delay(10);
                }
            }
        }

        public override void Dispose() {
            _running = false;

            try { _listenSocket?.Close(); } catch { }
            try { _listenSocket?.Dispose(); } catch { }

            _listenSocket = null;
        }
    }
}
