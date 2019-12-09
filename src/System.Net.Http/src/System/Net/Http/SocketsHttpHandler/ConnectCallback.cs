using System.Threading.Tasks;
using System.Threading;
using System.IO;

namespace System.Net.Http
{
    public delegate ValueTask<Stream> ConnectCallback(string host, int port, CancellationToken cancellationToken);
}
