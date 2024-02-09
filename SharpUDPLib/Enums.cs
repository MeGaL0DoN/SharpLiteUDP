namespace SharpLiteUDP
{
    enum UdpHeader
    {
        Reliable,
        Unreliable,
        Ping,
        ConnectRequest,
        ConnectionAccept,
        Disconnection,
        Acknowledgment
    }
}
