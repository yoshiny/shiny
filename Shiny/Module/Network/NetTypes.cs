using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    public readonly struct ConnectionInfo {
        public long ConnectionId { get; init; }
        public int ServiceId { get; init; }
        public string ServiceName { get; init; }
        public EndPoint? LocalEndPoint { get; init; }
        public EndPoint? RemoteEndPoint { get; init; }
    }

    public readonly struct NetPacket {
        public ReadOnlyMemory<byte> Payload { get; }

        public NetPacket(ReadOnlyMemory<byte> payload) {
            Payload = payload;
        }
    }

    public readonly struct EncodedBuffer {
        public byte[]? Array { get; }
        public int Offset { get; }
        public int Length { get; }
        public IDisposable? Owner { get; }

        public bool IsValid => Array != null && Length > 0;

        public EncodedBuffer(byte[]? array, int offset, int length, IDisposable? owner = null) {
            Array = array;
            Offset = offset;
            Length = length;
            Owner = owner;
        }

        public void Release() {
            Owner?.Dispose();
        }
    }
}
