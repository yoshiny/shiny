using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public sealed class TcpListenOptions {
        public string Name { get; init; } = string.Empty;
        public string Host { get; init; } = "0.0.0.0";
        public int Port { get; init; }

        public int Backlog { get; init; } = 512;
        public int AcceptSocketCount { get; init; } = 1;

        public int ReceiveBufferSize { get; init; } = 64 * 1024;
        public int SendBufferSize { get; init; } = 64 * 1024;

        public bool NoDelay { get; init; } = true;
        public bool KeepAlive { get; init; } = true;

        public int MaxConnections { get; init; } = 100_000;

        public INetProtocol Protocol { get; init; } = default!;
        public INetMessageAdapter MessageAdapter { get; init; } = default!;
    }
}
