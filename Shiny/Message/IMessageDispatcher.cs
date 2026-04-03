using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Message {
    public interface IMessageDispatcher {
        void Register<TMessage>(Action<TMessage> handler) where TMessage : class, IServerMessage;

        void Dispatch(IServerMessage message);
    }
}
