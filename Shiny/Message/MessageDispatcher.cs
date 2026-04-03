using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Message {
    public sealed class MessageDispatcher : IMessageDispatcher {
        private readonly Dictionary<Type, Action<IServerMessage>> m_Handlers = new();

        void IMessageDispatcher.Register<TMessage>(Action<TMessage> handler) {
            ArgumentNullException.ThrowIfNull(handler);

            var messageType = typeof(TMessage);
            if (m_Handlers.ContainsKey(messageType)) {
                throw new InvalidOperationException($"Handler already registered for message type: {messageType.FullName}");
            }

            m_Handlers[messageType] = msg => handler((TMessage)msg);
        }

        public void Dispatch(IServerMessage message) {
            ArgumentNullException.ThrowIfNull(message);

            var messageType = message.GetType();
            if (m_Handlers.TryGetValue(messageType, out var handler)) {
                handler(message);
                return;
            }

            throw new InvalidOperationException($"No handler registered for message type: {messageType.FullName}");
        }
    }
}
