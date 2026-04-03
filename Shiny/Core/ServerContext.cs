using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Feature;
using Shiny.Message;

namespace Shiny.Core {
    public sealed class ServerContext {
        private readonly Server m_Server;

        internal ServerContext(Server server) {
            m_Server = server;
        }

        public IMessageDispatcher Dispatcher => m_Server.Dispatcher;
        public long CurrentFrame => m_Server.CurrentFrame;
        public long TickInterval => m_Server.TickInterval;

        public void PostMessage(IServerMessage message) {
            m_Server.PostMessage(message);
        }

        public void Post(Action action) {
            ArgumentNullException.ThrowIfNull(action);
            m_Server.PostContinuation(_ => action(), null);
        }

        public void RunAsync(Func<Task> asyncAction) {
            m_Server.RunAsync(asyncAction);
        }

        public void RunAsync<TState>(TState state, Func<TState, Task> asyncAction) {
            m_Server.RunAsync<TState>(state, asyncAction);
        }

        public TModule GetModule<TModule>() where TModule : class, IModule {
            return m_Server.GetModule<TModule>();
        }

        public TComponent GetComponent<TComponent>() where TComponent : class, IComponent {
            return m_Server.GetComponent<TComponent>();
        }
    }
}
