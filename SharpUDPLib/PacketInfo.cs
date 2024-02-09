using System.Net;

namespace SharpLiteUDP
{
    record struct PacketInfo(IPEndPoint EndPoint, uint SequenceNumber);
}