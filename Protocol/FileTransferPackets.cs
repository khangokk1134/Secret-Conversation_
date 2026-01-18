using System;

namespace Protocol
{
    
    public class FileOfferPacket : PacketBase
    {
        public string FileId { get; set; } = "";
        public string FromId { get; set; } = "";
        public string ToId { get; set; } = "";

        public string FileName { get; set; } = "";
        public long FileSize { get; set; }

        public string MimeType { get; set; } = "application/octet-stream";
        public bool IsImage { get; set; }

        public FileOfferPacket() { Type = PacketType.FileOffer; }
    }

    
    public class FileAcceptPacket : PacketBase
    {
        public string FileId { get; set; } = "";
        public string FromId { get; set; } = ""; 
        public string ToId { get; set; } = "";  

        public FileAcceptPacket() { Type = PacketType.FileAccept; }
    }

   
    public class FileChunkPacket : PacketBase
    {
        public string FileId { get; set; } = "";
        public string FromId { get; set; } = "";
        public string ToId { get; set; } = "";

        public int Index { get; set; }
        public bool IsLast { get; set; }

        public byte[] Data { get; set; } = Array.Empty<byte>();

        public FileChunkPacket() { Type = PacketType.FileChunk; }
    }

    
    public class FileCompletePacket : PacketBase
    {
        public string FileId { get; set; } = "";
        public string FromId { get; set; } = "";
        public string ToId { get; set; } = "";

        public FileCompletePacket() { Type = PacketType.FileComplete; }
    }
}
