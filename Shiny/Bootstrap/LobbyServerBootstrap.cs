using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Shiny.Core;
using Shiny.Module.Network;

namespace Shiny.Bootstrap {
    public sealed class LobbyServerBootstrap : IServerBootstrap {
        public void Build(ServerBuilder builder) {
            builder.AddModule<NetModule>()
                .OnStart( sc => Log.Information( "LobbyServer Start." ) )
                ;
        }
    }
}
