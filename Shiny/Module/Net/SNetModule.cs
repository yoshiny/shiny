using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Net {
    public sealed class SPacketLease : IDisposable {
        private byte[]? _buffer;

        public SPacketLease(byte[] buffer, int length) {
            _buffer = buffer;
            Length = length;
        }

        public int Length { get; }
        public Memory<byte> Memory => _buffer!.AsMemory(0, Length);
        public Span<byte> Span => _buffer!.AsSpan(0, Length);

        public void Dispose() {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public sealed class SPacketPool {
        public static SPacketPool Shared { get; } = new();

        public SPacketLease Rent(int length) {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            return new SPacketLease(buffer, length);
        }
    }

    public sealed class SNetPacket : IDisposable {
        public byte[] Buffer { get; }
        public int Offset { get; }
        public int Length { get; }
        public bool ReturnBufferToPool { get; }
        public int Sent;

        public SNetPacket(byte[] buffer, int offset, int length, bool returnBufferToPool) {
            Buffer = buffer;
            Offset = offset;
            Length = length;
            ReturnBufferToPool = returnBufferToPool;
        }

        public void Dispose() {
            if (ReturnBufferToPool) {
                ArrayPool<byte>.Shared.Return(Buffer);
            }
        }
    }

    public static class SLimitedLengthCodec {
        public const int HeaderSize = 4;
        public const int MaxPacketSize = 1024 * 1024;

        public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> payload) {
            payload = default;
            if (buffer.Length < HeaderSize) {
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            buffer.Slice(0, HeaderSize).CopyTo(header);
            int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (bodyLength <= 0 || bodyLength > MaxPacketSize) {
                throw new InvalidOperationException($"Invalid packet body length: {bodyLength}");
            }

            if (buffer.Length < HeaderSize + bodyLength) {
                return false;
            }

            payload = buffer.Slice(HeaderSize, bodyLength);
            buffer = buffer.Slice(HeaderSize + bodyLength);

            return true;
        }
    }


    public interface ITcpPacketDispatcher {
        void OnConnected(STcpConnection connection);
        void OnDisconnected(STcpConnection connection);
        void Dispatch(STcpConnection connection, SPacketLease packet);
    }
}
