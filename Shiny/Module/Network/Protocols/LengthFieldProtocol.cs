using Shiny.Module.Network.Internal;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network.Protocols {
    public sealed class LengthFieldProtocol : INetProtocol {
        public INetFrameDecoder CreateDecoder() => new Decoder();
        public INetFrameEncoder CreateEncoder() => new Encoder();

        private sealed class Decoder : INetFrameDecoder {
            public bool TryDecode(ref ReadOnlySequence<byte> buffer, out NetPacket packet) {
                packet = default;

                if (buffer.Length < 4)
                    return false;

                var reader = new SequenceReader<byte>(buffer);
                if (!reader.TryReadLittleEndian(out int length))
                    return false;

                if (length < 0)
                    throw new InvalidOperationException("Protocol length is negative.");

                if (buffer.Length < 4 + length)
                    return false;

                var payload = buffer.Slice(4, length).ToArray();
                buffer = buffer.Slice(4 + length);

                packet = new NetPacket(payload);
                return true;
            }
        }

        private sealed class Encoder : INetFrameEncoder {
            public EncodedBuffer Encode<T>(T message) {
                byte[] body = message switch {
                    byte[] arr => arr,
                    ReadOnlyMemory<byte> mem => mem.ToArray(),
                    ArraySegment<byte> seg => seg.Array is not null
                        ? seg.AsMemory().ToArray()
                        : Array.Empty<byte>(),
                    _ => throw new InvalidOperationException($"Unsupported message type: {typeof(T).FullName}")
                };

                int total = 4 + body.Length;
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(total);

                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), body.Length);
                body.CopyTo(buf.AsSpan(4));

                return new EncodedBuffer(
                    buf,
                    0,
                    total,
                    new ArrayPoolBufferOwner(buf));
            }
        }
    }
}
