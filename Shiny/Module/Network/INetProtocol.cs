using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public interface INetProtocol {
        INetFrameDecoder CreateDecoder();
        INetFrameEncoder CreateEncoder();
    }

    public interface INetFrameDecoder {
        bool TryDecode(ref ReadOnlySequence<byte> buffer, out NetPacket packet);
    }

    public interface INetFrameEncoder {
        EncodedBuffer Encode<T>(T message);
    }
}
