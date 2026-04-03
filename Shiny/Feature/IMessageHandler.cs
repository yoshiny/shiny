using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shiny.Message;

namespace Shiny.Feature {
    public interface IMessageHandler<in IMessage>
        where IMessage : class, IServerMessage
    {
    }
}
