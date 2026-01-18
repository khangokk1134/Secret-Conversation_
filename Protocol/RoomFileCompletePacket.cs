namespace Protocol
{
    public class RoomFileCompletePacket : PacketBase
    {
        public RoomFileCompletePacket() { Type = PacketType.RoomFileComplete; }

        public string RoomId { get; set; } = "";
        public string FileId { get; set; } = "";
        public string FromId { get; set; } = "";
        public string ToId { get; set; } = "";
    }
}
