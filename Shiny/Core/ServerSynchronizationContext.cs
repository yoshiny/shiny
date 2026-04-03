using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Core {
    public sealed class ServerSynchronizationContext : SynchronizationContext {
        private readonly ContinuationQueue m_ContinuationQueue;
        private readonly Action m_Signal;

        public ServerSynchronizationContext(ContinuationQueue continuationQueue, Action signal) {
            m_ContinuationQueue = continuationQueue ?? throw new ArgumentNullException(nameof(continuationQueue));
            m_Signal = signal ?? throw new ArgumentNullException(nameof(signal));
        }

        public override void Post(SendOrPostCallback d, object? state) {
            m_ContinuationQueue.Enqueue(d, state);
            m_Signal();
        }

        public override void Send(SendOrPostCallback d, object? state) {
            // 单线程逻辑线程模型下，通常不建议外部线程同步 Send 进来。
            // 这里直接退化为 Post，避免死锁和重入风险。
            Post(d, state);
        }
    }
}
