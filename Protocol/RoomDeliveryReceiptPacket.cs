using System;

namespace Protocol
{
    public class RoomDeliveryReceiptPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string FromId { get; set; } = "";  
        public string ToId { get; set; } = "";     
        public string Status { get; set; } = "delivered_to_client";
        public long Timestamp { get; set; }

        public RoomDeliveryReceiptPacket()
        {
            Type = PacketType.RoomDeliveryReceipt;
        }
    }
}
