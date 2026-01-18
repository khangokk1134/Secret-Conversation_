namespace Protocol
{
    public sealed class SeenReceiptPacket : PacketBase
    {
        public string MessageId { get; set; } = "";

      
        public string FromId { get; set; } = ""; 
        public string ToId { get; set; } = "";   

        public long Timestamp { get; set; }

        public SeenReceiptPacket()
        {
            Type = PacketType.SeenReceipt;
        }
    }
}
