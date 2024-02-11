namespace SharpLiteUDP
{
    public enum UDPDeliveryMethod
    {
        Reliable,
        Unreliable,
    }
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
