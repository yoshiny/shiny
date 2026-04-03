using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Core {
    public readonly struct TickContext {
        public long FrameIndex { get; init; }
        public long NowTimestamp { get; init; }
        public long TickInterval { get; init; }
    }
}
