using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Protocol
{
    public static class PacketIO
    {
        public static void SendJson(NetworkStream ns, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes(payload.Length);

            ns.Write(len, 0, 4);
            ns.Write(payload, 0, payload.Length);
            ns.Flush();
        }

        public static void SendPacket<T>(NetworkStream ns, T packet)
        {
            var json = JsonSerializer.Serialize(packet);
            SendJson(ns, json);
        }

        public static string? ReadJson(NetworkStream ns)
        {
            Span<byte> lenBuf = stackalloc byte[4];
            if (!ReadExact(ns, lenBuf)) return null;

            int len = BitConverter.ToInt32(lenBuf);
            if (len <= 0 || len > 10_000_000) return null;

            byte[] payload = new byte[len];
            if (!ReadExact(ns, payload)) return null;

            return Encoding.UTF8.GetString(payload);
        }

        private static bool ReadExact(NetworkStream ns, Span<byte> buf)
        {
            int offset = 0;
            while (offset < buf.Length)
            {
                int r = ns.Read(buf.Slice(offset));
                if (r <= 0) return false;
                offset += r;
            }
            return true;
        }

        private static bool ReadExact(NetworkStream ns, byte[] buf)
        {
            int offset = 0;
            while (offset < buf.Length)
            {
                int r = ns.Read(buf, offset, buf.Length - offset);
                if (r <= 0) return false;
                offset += r;
            }
            return true;
        }
    }
}
