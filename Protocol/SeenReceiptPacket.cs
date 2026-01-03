namespace Protocol
{
    public sealed class SeenReceiptPacket : PacketBase
    {
        public string MessageId { get; set; } = "";

        // Viewer (người xem) -> Sender (người gửi)
        public string FromId { get; set; } = ""; // viewer id
        public string ToId { get; set; } = "";   // sender id

        public long Timestamp { get; set; }

        public SeenReceiptPacket()
        {
            Type = PacketType.SeenReceipt;
        }
    }
}
