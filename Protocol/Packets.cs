using System;

namespace Protocol
{
    // ================= REGISTER =================
    public class RegisterPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;

        public RegisterPacket()
        {
            Type = PacketType.Register;
        }
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

        // Sender nên set MessageId khi gửi; JSON deserialize sẽ overwrite giá trị này
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public ChatPacket()
        {
            Type = PacketType.Chat;
        }
    }

    // ================= TYPING =================
    public class TypingPacket : PacketBase
    {
        public string FromId { get; set; } = string.Empty;
        public string ToId { get; set; } = string.Empty;
        public string FromUser { get; set; } = string.Empty;
        public bool IsTyping { get; set; }

        public TypingPacket()
        {
            Type = PacketType.Typing;
        }
    }

    // ================= GET PUBLIC KEY =================
    public class GetPublicKeyPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;

        // optional: người đang yêu cầu key (server có thể không dùng)
        public string FromId { get; set; } = string.Empty;

        public GetPublicKeyPacket()
        {
            Type = PacketType.GetPublicKey;
        }
    }

    // ================= PUBLIC KEY =================
    public class PublicKeyPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;

        public PublicKeyPacket()
        {
            Type = PacketType.PublicKey;
        }
    }

    // ================= ACK (SERVER -> SENDER) =================
    public class ChatAckPacket : PacketBase
    {
        public string MessageId { get; set; } = "";
        public string FromId { get; set; } = ""; // sender
        public string ToId { get; set; } = "";   // receiver

        // recommended statuses:
        // accepted | delivered | offline_saved | delivered_to_client
        // (timeout is client-side only)
        public string Status { get; set; } = "";

        public ChatAckPacket()
        {
            Type = PacketType.ChatAck;
        }
    }

    // ================= DELIVERY RECEIPT (RECEIVER -> SERVER) =================
    public class DeliveryReceiptPacket : PacketBase
    {
        public string MessageId { get; set; } = "";
        public string FromId { get; set; } = "";   // receiver
        public string ToId { get; set; } = "";     // original sender

        // for now we use: received
        // (later can extend: delivered/read, but server currently only handles "received")
        public string Status { get; set; } = "";

        public long Timestamp { get; set; }

        public DeliveryReceiptPacket()
        {
            Type = PacketType.DeliveryReceipt;
        }
    }

    // ================= USER LIST =================
    public class UserListPacket : PacketBase
    {
        public UserInfo[] Users { get; set; } = Array.Empty<UserInfo>();

        public UserListPacket()
        {
            Type = PacketType.UserList;
        }
    }

    public class UserInfo
    {
        public string ClientId { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public bool Online { get; set; }
    }

    // ================= LOGOUT =================
    public class LogoutPacket : PacketBase
    {
        public string ClientId { get; set; } = string.Empty;

        public LogoutPacket()
        {
            Type = PacketType.Logout;
        }
    }

    // ================= RECALL =================
    public class RecallPacket : PacketBase
    {
        public string FromId { get; set; } = string.Empty;
        public string ToId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;

        public RecallPacket()
        {
            Type = PacketType.Recall;
        }
    }
}
