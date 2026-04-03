using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Shiny.Core;

namespace Shiny.Hosting {
    public sealed class ServerHost {
        private readonly Server m_Server;
        private readonly ServerHostOptions m_Options;
        private Thread? m_Thread;
        private volatile ServerHostState m_State = ServerHostState.Created;
        private Exception? m_FatalException;

        public string ThreadName => m_Options.ThreadName;
        public ServerHostState State => m_State;
        public bool IsRunning => m_State == ServerHostState.Running;
        public bool IsFaulted => m_State == ServerHostState.Faulted;
        public Exception? FatalException => m_FatalException;

        public ServerHost(Server server, ServerHostOptions options) {
            m_Server = server ?? throw new ArgumentNullException(nameof(server));
            m_Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Start() {
            if (m_Thread != null) {
                throw new InvalidOperationException($"ServerHost already started: {m_Options.ThreadName}");
            }

            m_Thread = new Thread(ThreadMain) {
                Name = m_Options.ThreadName,
                IsBackground = m_Options.IsBackground,
                Priority = m_Options.Priority,
            };

            m_Thread.Start();
        }

        public void Stop() {
            if (m_State != ServerHostState.Running) {
                return;
            }
            m_Server.RequestStop();
        }

        public bool Join(int timeoutMs = Timeout.Infinite ) {
            var thread = m_Thread;
            if (thread == null) {
                return true;
            }
            return thread.Join(timeoutMs);
        }

        private void ThreadMain() {
            try {
                m_State = ServerHostState.Running;
                m_Server.Run();
                if (m_State == ServerHostState.Running) {
                    m_State = ServerHostState.Stopped;
                }
            } catch (Exception ex) {
                m_FatalException = ex;
                m_State = ServerHostState.Faulted;
                Log.Fatal("Server.Run Raise {Exception}", ex);
            }
        }
    }
}
