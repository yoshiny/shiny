using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Hosting {
    public sealed class ServerProcessHost {
        private readonly List<ServerHost> m_Hosts = new();

        public IReadOnlyList<ServerHost> Hosts => m_Hosts;

        public void Add(ServerHost host) {
            ArgumentNullException.ThrowIfNull(host);
            m_Hosts.Add(host);
        }

        public void StartAll() {
            foreach (ServerHost host in m_Hosts) {
                host.Start();
            }
        }

        public void StopAll() {
            foreach(ServerHost host in m_Hosts) {
                host.Stop();
            }
        }

        public void JoinAll(int timeoutMs = Timeout.Infinite) {
            foreach (ServerHost host in m_Hosts) {
                host.Join(timeoutMs);
            }
        }
    }
}
