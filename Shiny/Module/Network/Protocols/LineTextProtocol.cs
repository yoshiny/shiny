using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network.Protocols {
    public sealed class LineTextProtocol : INetProtocol {
        public INetFrameDecoder CreateDecoder() => new Decoder();
        public INetFrameEncoder CreateEncoder() => new Encoder();

        private sealed class Decoder : INetFrameDecoder {
            public bool TryDecode(ref ReadOnlySequence<byte> buffer, out NetPacket packet) {
                packet = default;

                var reader = new SequenceReader<byte>(buffer);
                if (!reader.TryReadTo(out ReadOnlySequence<byte> line, (byte)'\n'))
                    return false;

                var bytes = line.ToArray();
                buffer = buffer.Slice(reader.Position);

                if (bytes.Length > 0 && bytes[^1] == (byte)'\r') {
                    Array.Resize(ref bytes, bytes.Length - 1);
                }

                packet = new NetPacket(bytes);
                return true;
            }
        }

        private sealed class Encoder : INetFrameEncoder {
            public EncodedBuffer Encode<T>(T message) {
                string text = message switch {
                    string s => s,
                    _ => message?.ToString() ?? string.Empty
                };

                byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
                return new EncodedBuffer(bytes, 0, bytes.Length);
            }
        }
    }
}
