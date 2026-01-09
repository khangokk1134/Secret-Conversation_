namespace Protocol
{
    public class LeaveRoomPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string ClientId { get; set; } = "";

        public LeaveRoomPacket()
        {
            Type = PacketType.LeaveRoom;
        }
    }
}
