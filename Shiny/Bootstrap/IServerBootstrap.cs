using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Core;

namespace Shiny.Bootstrap {
    public interface IServerBootstrap {
        void Build(ServerBuilder builder);
    }
}
