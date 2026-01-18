namespace Protocol
{
    public class RoomFileChunkPacket : PacketBase
    {
        public RoomFileChunkPacket() { Type = PacketType.RoomFileChunk; }

        public string RoomId { get; set; } = "";
        public string FileId { get; set; } = "";
        public string FromId { get; set; } = ""; 
        public string ToId { get; set; } = "";   
        public int Index { get; set; }
        public bool IsLast { get; set; }
        public byte[] Data { get; set; } = System.Array.Empty<byte>();
    }
}
