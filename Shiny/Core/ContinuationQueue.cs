using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Core {
    public sealed class ContinuationQueue {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> m_Queue = new();

        public void Enqueue(SendOrPostCallback callback, object? state) {
            m_Queue.Enqueue((callback, state));
        }

        public int Drain(int maxCount = int.MaxValue) {
            int count = 0;
            while (count < maxCount && m_Queue.TryDequeue(out var item)) {
                item.Callback(item.State);
                count++;
            }
            return count;
        }
    }
}
