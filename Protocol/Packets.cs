using System.Collections.Generic;

namespace Protocol
{
    public class RegisterPacket : PacketBase
    {
        public string User { get; set; }
        public string PublicKey { get; set; }

        public RegisterPacket()
        {
            Type = PacketType.Register;
        }
    }

    public class ChatPacket : PacketBase
    {
        public string From { get; set; }
        public string To { get; set; }
        public string EncKey { get; set; }
        public string EncMsg { get; set; }
        public string Sig { get; set; }

        public ChatPacket()
        {
            Type = PacketType.Chat;
        }
    }

    public class TypingPacket : PacketBase
    {
        public string From { get; set; }
        public string To { get; set; }
        public bool IsTyping { get; set; }

        public TypingPacket()
        {
            Type = PacketType.Typing;
        }
    }

    public class GetPublicKeyPacket : PacketBase
    {
        public string User { get; set; }

        public GetPublicKeyPacket()
        {
            Type = PacketType.GetPublicKey;
        }
    }

    public class PublicKeyPacket : PacketBase
    {
        public string User { get; set; }
        public string PublicKey { get; set; }

        public PublicKeyPacket()
        {
            Type = PacketType.PublicKey;
        }
    }

    public class UserListPacket : PacketBase
    {
        public List<string> Users { get; set; }

        public UserListPacket()
        {
            Type = PacketType.UserList;
        }
    }
}
