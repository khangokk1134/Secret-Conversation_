using System;

namespace Protocol
{
    public class RoomFileOfferPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string FileId { get; set; } = "";

        public string FromId { get; set; } = "";
        public string FromUser { get; set; } = "";

        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public string MimeType { get; set; } = "";
        public bool IsImage { get; set; }

        public RoomFileOfferPacket()
        {
            Type = PacketType.RoomFileOffer;
        }
    }
}
