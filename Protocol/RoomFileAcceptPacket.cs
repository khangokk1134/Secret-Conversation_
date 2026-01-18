namespace Protocol
{
    public class RoomFileAcceptPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string FileId { get; set; } = "";

        public string FromId { get; set; } = ""; 
        public string ToId { get; set; } = "";   

        public RoomFileAcceptPacket()
        {
            Type = PacketType.RoomFileAccept;
        }
    }
}
