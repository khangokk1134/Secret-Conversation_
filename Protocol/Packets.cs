using System;
using System.Collections.Generic;

namespace Protocol
{
    // ================= REGISTER =================
    public class RegisterPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;

        public RegisterPacket() { Type = PacketType.Register; }
    }

    // ================= CHAT =================
    public class ChatPacket : PacketBase
    {
        public string FromId { get; set; } = string.Empty;
        public string ToId { get; set; } = string.Empty;
        public string FromUser { get; set; } = string.Empty;
        public string ToUser { get; set; } = string.Empty;

        public string EncKey { get; set; } = string.Empty;
        public string EncMsg { get; set; } = string.Empty;
        public string Sig { get; set; } = string.Empty;

        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public ChatPacket() { Type = PacketType.Chat; }
    }

    
    public class TypingPacket : PacketBase
    {
        public string FromId { get; set; } = string.Empty;
        public string ToId { get; set; } = string.Empty;
        public string FromUser { get; set; } = string.Empty;
        public bool IsTyping { get; set; }

        public TypingPacket() { Type = PacketType.Typing; }
    }

   
    public class GetPublicKeyPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;
        public string FromId { get; set; } = string.Empty;

        public GetPublicKeyPacket() { Type = PacketType.GetPublicKey; }
    }

    
    public class PublicKeyPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;

        public PublicKeyPacket() { Type = PacketType.PublicKey; }
    }

    
    public class ChatAckPacket : PacketBase
    {
        public string MessageId { get; set; } = "";
        public string FromId { get; set; } = ""; // sender
        public string ToId { get; set; } = "";   // receiver
        public string Status { get; set; } = "";

        public ChatAckPacket() { Type = PacketType.ChatAck; }
    }

    
    public class DeliveryReceiptPacket : PacketBase
    {
        public string MessageId { get; set; } = "";
        public string FromId { get; set; } = "";   // receiver
        public string ToId { get; set; } = "";     // original sender
        public string Status { get; set; } = "";   // now: delivered_to_client
        public long Timestamp { get; set; }

        public DeliveryReceiptPacket() { Type = PacketType.DeliveryReceipt; }
    }

   
    public class UserListPacket : PacketBase
    {
        public UserInfo[] Users { get; set; } = Array.Empty<UserInfo>();
        public UserListPacket() { Type = PacketType.UserList; }
    }

    public class UserInfo
    {
        public string ClientId { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public bool Online { get; set; }
    }

    
    public class LogoutPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;
        public LogoutPacket() { Type = PacketType.Logout; }
    }

    
    public class RecallPacket : PacketBase
    {
        public string FromId { get; set; } = string.Empty;
        public string ToId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;

        public RecallPacket() { Type = PacketType.Recall; }
    }

    
    public class CreateRoomPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string CreatorId { get; set; } = "";
        public string[] MemberIds { get; set; } = Array.Empty<string>();

        public CreateRoomPacket() { Type = PacketType.CreateRoom; }
    }

    
    public class RoomInfoPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string[] MemberIds { get; set; } = Array.Empty<string>();

        public RoomInfoPacket() { Type = PacketType.RoomInfo; }
    }

    
    public class RoomChatPacket : PacketBase
    {
        public string RoomId { get; set; } = "";

        public string FromId { get; set; } = "";
        public string FromUser { get; set; } = "";

        
        public string EncMsg { get; set; } = "";

        
        public Dictionary<string, string> EncKeys { get; set; } = new();

        public string Sig { get; set; } = "";

        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public RoomChatPacket() { Type = PacketType.RoomChat; }
    }

    
    public class RoomAckPacket : PacketBase
    {
        public string RoomId { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string FromId { get; set; } = "";
        public string Status { get; set; } = "";
        public RoomAckPacket() { Type = PacketType.RoomAck; }
    }
}