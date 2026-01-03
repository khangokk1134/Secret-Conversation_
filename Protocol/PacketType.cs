namespace Protocol
{
    public enum PacketType
    {
        Register,
        Chat,
        Typing,
        Recall,
        GetPublicKey,
        PublicKey,
        UserList,
        Logout,
        ChatAck,
        DeliveryReceipt,
        SeenReceipt = 50
    }
}
