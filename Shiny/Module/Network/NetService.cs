using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shiny.Module.Network {
    internal enum NetServiceKind {
        Listener = 1,
        Connector = 2
    }

    internal abstract class NetService : IDisposable {
        protected readonly NetModule Owner;

        public int ServiceId { get; }
        public string Name { get; }
        public NetServiceKind Kind { get; }

        public INetProtocol Protocol { get; }
        public INetMessageAdapter MessageAdapter { get; }

        protected NetService( NetModule owner, int serviceId, string name, NetServiceKind kind, INetProtocol protocol, INetMessageAdapter messageAdapter) {
            Owner = owner;
            ServiceId = serviceId;
            Name = name;
            Kind = kind;
            Protocol = protocol;
            MessageAdapter = messageAdapter;
        }

        public abstract void Start();
        public abstract void Dispose();
    }
}
