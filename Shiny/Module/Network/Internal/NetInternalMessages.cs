using Shiny.Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network.Internal {
    internal sealed class NetAcceptedInternalMessage : IServerMessage{
        public int ServiceId { get; }
        public Socket Socket { get; }
        public int ReceiveBufferSize { get; }

        public NetAcceptedInternalMessage(int serviceId, Socket socket, int receiveBufferSize) {
            ServiceId = serviceId;
            Socket = socket;
            ReceiveBufferSize = receiveBufferSize;
        }
    }

    internal sealed class NetConnectedInternalMessage : IServerMessage {
        public int ServiceId { get; }
        public Socket Socket { get; }
        public int ReceiveBufferSize { get; }

        public NetConnectedInternalMessage(int serviceId, Socket socket, int receiveBufferSize) {
            ServiceId = serviceId;
            Socket = socket;
            ReceiveBufferSize = receiveBufferSize;
        }
    }

    internal sealed class NetConnectFailedInternalMessage : IServerMessage {
        public int ServiceId { get; }
        public Exception Exception { get; }

        public NetConnectFailedInternalMessage(int serviceId, Exception exception) {
            ServiceId = serviceId;
            Exception = exception;
        }
    }

    internal sealed class NetReceivedInternalMessage : IServerMessage {
        public long ConnectionId { get; }
        public NetPacket Packet { get; }

        public NetReceivedInternalMessage(long connectionId, NetPacket packet) {
            ConnectionId = connectionId;
            Packet = packet;
        }
    }

    internal sealed class NetClosedInternalMessage : IServerMessage {
        public long ConnectionId { get; }
        public NetCloseReason Reason { get; }

        public NetClosedInternalMessage(long connectionId, NetCloseReason reason) {
            ConnectionId = connectionId;
            Reason = reason;
        }
    }
}
