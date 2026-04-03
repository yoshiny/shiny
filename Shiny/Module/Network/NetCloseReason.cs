using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public enum NetCloseReason {
        Unknown = 0,
        RemoteClosed = 1,
        LocalClosed = 2,
        ConnectFailed = 3,
        SendError = 4,
        ReceiveError = 5,
        ProtocolError = 6,
        ServiceStopped = 7
    }
}
