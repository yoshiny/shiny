using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network.Internal {
    internal sealed class ArrayPoolBufferOwner : IDisposable {
        private byte[]? _buffer;

        public ArrayPoolBufferOwner(byte[] buffer) {
            _buffer = buffer;
        }

        public void Dispose() {
            var buf = Interlocked.Exchange(ref _buffer, null);
            if (buf != null) {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }
}
