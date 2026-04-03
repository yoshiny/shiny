using Shiny.Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public interface INetMessageAdapter {
        IServerMessage CreateConnectedMessage(ConnectionInfo connection);
        IServerMessage CreateDisconnectedMessage(ConnectionInfo connection, NetCloseReason reason);
        IServerMessage CreateReceiveMessage(ConnectionInfo connection, NetPacket packet);
        IServerMessage CreateConnectFailedMessage(int serviceId, string serviceName, Exception exception);
    }
}
