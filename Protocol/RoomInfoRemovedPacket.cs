namespace Protocol
{
    public class RoomInfoRemovedPacket : PacketBase
    {
        public string RoomId { get; set; } = "";

        public RoomInfoRemovedPacket()
        {
            Type = PacketType.RoomInfoRemoved;
        }
    }
}
