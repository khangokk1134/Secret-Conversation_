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

    public class ChatAckPacket : PacketBase
    {
        public string FromId { get; set; } = "";
        public string ToId { get; set; } = "";
        public string Status { get; set; } = ""; // delivered | offline | error

        public ChatAckPacket()
        {
            Type = PacketType.ChatAck;
        }
    }

    // ================= USER LIST =================
    public class UserListPacket : PacketBase
    {
        public UserInfo[] Users { get; set; } = new UserInfo[0];

        public UserListPacket()
        {
            Type = PacketType.UserList;
        }
    }

    public class UserInfo
    {
        public string ClientId { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
    }
}
