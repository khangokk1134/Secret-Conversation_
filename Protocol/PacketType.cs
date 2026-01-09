namespace Protocol
{
    public enum PacketType
    {
        Register = 1,
        UserList = 2,
        GetPublicKey = 3,
        PublicKey = 4,

        Chat = 10,
        ChatAck = 11,
        Typing = 12,
        Recall = 13,

        DeliveryReceipt = 20,
        SeenReceipt = 21,

        Logout = 30,

        // ===== GROUP =====
        CreateRoom = 40,
        RoomInfo = 41,
        RoomChat = 42,
        RoomAck = 43,

        RoomDeliveryReceipt = 44,
        LeaveRoom = 45,
        RoomInfoRemoved = 46,
    }
}
