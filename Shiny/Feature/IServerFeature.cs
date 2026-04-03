using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Core;

namespace Shiny.Feature {
    public interface IServerFeature {
        void OnInit(ServerContext context);
        void OnStart();
        void OnStop();
    }

    public interface IModule : IServerFeature {
    }

    public interface IComponent : IServerFeature {
    }
    public interface IPreTickable {
        void OnPreTick();
    }
    public interface ITickable {
        void OnTick(in TickContext tick);
    }
    public interface IPostTickable {
        void OnPostTick();
    }
}
