using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Core;

namespace Shiny.Feature {
    public sealed class ModuleManager {
        private readonly FeatureCollection<IModule> m_Features = new();

        public void Add(IModule module) => m_Features.Add(module);

        public T Get<T>() where T : class, IModule => m_Features.Get<T>();

        public T? TryGet<T>() where T : class, IModule => m_Features.TryGet<T>();

        public void InitAll(ServerContext context) => m_Features.InitAll(context);

        public void StartAll() => m_Features.StartAll();

        public void StopAllReverse() => m_Features.StopAllReverse();

        public void RunPreTick() => m_Features.RunPreTick();

        public void RunTick(in TickContext tick) => m_Features.RunTick(tick);

        public void RunPostTick() => m_Features.RunPostTick();
    }
}
