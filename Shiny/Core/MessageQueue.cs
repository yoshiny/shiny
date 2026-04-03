using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Message;

namespace Shiny.Core {
    public sealed class MessageQueue {
        private readonly ConcurrentQueue<IServerMessage> m_Queue = new();

        public void Enqueue(IServerMessage message) {
            m_Queue.Enqueue(message);
        }

        public bool TryDequeue(out IServerMessage? message) {
            return m_Queue.TryDequeue(out message);
        }

        public int Drain(Action<IServerMessage> action, int maxCount = int.MaxValue) {
            ArgumentNullException.ThrowIfNull(action);

            int count = 0;
            while (count < maxCount && m_Queue.TryDequeue(out var msg)) {
                action(msg);
                count++;
            }

            return count;
        }
    }
}
