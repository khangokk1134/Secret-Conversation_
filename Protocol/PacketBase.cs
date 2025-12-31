using System.Text.Json.Serialization;

namespace Protocol
{
    // Base packet để server/client đọc Type trước rồi mới deserialize packet cụ thể
    public class PacketBase
    {
        // IMPORTANT: phải public get;set; để JsonSerializer serialize/deserialize được
        public PacketType Type { get; set; }

        public PacketBase() { }
    }
}
