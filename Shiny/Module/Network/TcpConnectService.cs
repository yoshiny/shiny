using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    internal sealed class TcpConnectService : NetService {
        private readonly TcpConnectOptions _options;
        private volatile bool _running;

        public TcpConnectService(NetModule owner, int serviceId, TcpConnectOptions options)
            : base(owner, serviceId, options.Name, NetServiceKind.Connector, options.Protocol, options.MessageAdapter) {
            _options = options;
        }

        public override void Start() {
            if (_running)
                return;

            _running = true;
            _ = Task.Run(ConnectLoopAsync);
        }

        private async Task ConnectLoopAsync() {
            do {
                Socket? socket = null;

                try {
                    var ip = IPAddress.Parse(_options.RemoteHost);
                    var ep = new IPEndPoint(ip, _options.RemotePort);

                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) {
                        NoDelay = _options.NoDelay,
                        ReceiveBufferSize = _options.ReceiveBufferSize,
                        SendBufferSize = _options.SendBufferSize
                    };

                    if (_options.KeepAlive) {
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    }

                    await socket.ConnectAsync(ep);

                    var conn = Owner.RegisterConnectedOutbound(this, socket, _options.ReceiveBufferSize);
                    conn.Start();

                    return;
                } catch (Exception ex) {
                    socket?.Dispose();

                    var msg = MessageAdapter.CreateConnectFailedMessage(ServiceId, Name, ex);
                    Owner.PostToServer(msg);

                    if (!_options.AutoReconnect || !_running)
                        return;

                    await Task.Delay(_options.ReconnectDelayMs);
                }
            }
            while (_running);
        }

        public override void Dispose() {
            _running = false;
        }
    }
}
