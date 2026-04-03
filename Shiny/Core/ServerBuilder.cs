using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Feature;

namespace Shiny.Core {
    public sealed class ServerBuilder {
        private readonly List<Func<IModule>> m_ModuleFactories = new();
        private readonly List<Func<IComponent>> m_ComponentFactories = new();
        private readonly List<Action<ServerContext>> m_StartActions = new();

        public ServerBuilder AddModule<T>() where T : class, IModule, new() {
            m_ModuleFactories.Add(()=>new T());
            return this;
        }

        public ServerBuilder AddModule(IModule module) {
            ArgumentNullException.ThrowIfNull(module);
            m_ModuleFactories.Add(() => module);
            return this;
        }

        public ServerBuilder AddComponent<T>() where T : class, IComponent, new() {
            m_ComponentFactories.Add(() => new T());
            return this;
        }

        public ServerBuilder AddComponent(IComponent component) {
            ArgumentNullException.ThrowIfNull(component);
            m_ComponentFactories.Add(() => component);
            return this;
        }

        public ServerBuilder OnStart(Action<ServerContext> action) {
            ArgumentNullException.ThrowIfNull(action);
            m_StartActions.Add(action);
            return this;
        }

        internal void BuildInto(Server server) {
            foreach (var factory in m_ModuleFactories) {
                server.Modules.Add(factory());
            }
            foreach (var factory in m_ComponentFactories) {
                server.Components.Add(factory());
            }
            server.AddStartActions(m_StartActions);
        }
    }
}
