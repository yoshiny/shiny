using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Hosting {
    public sealed class ServerHostOptions {
        public required string ThreadName { get; init; }

        public bool IsBackground { get; init; } = false;
        public ThreadPriority Priority { get; init; } = ThreadPriority.Normal;
    }
}
