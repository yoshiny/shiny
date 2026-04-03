using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public sealed class TcpConnectOptions {
        public string Name { get; init; } = string.Empty;
        public string RemoteHost { get; init; } = string.Empty;
        public int RemotePort { get; init; }

        public int ReceiveBufferSize { get; init; } = 64 * 1024;
        public int SendBufferSize { get; init; } = 64 * 1024;

        public bool NoDelay { get; init; } = true;
        public bool KeepAlive { get; init; } = true;

        public bool AutoReconnect { get; init; } = false;
        public int ReconnectDelayMs { get; init; } = 3000;

        public INetProtocol Protocol { get; init; } = default!;
        public INetMessageAdapter MessageAdapter { get; init; } = default!;
    }
}
