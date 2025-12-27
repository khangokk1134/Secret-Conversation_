using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Protocol;

namespace ChatServer
{
    public class ClientConnection
    {
        private readonly TcpClient _tcp;
        private readonly Server _server;
        private readonly NetworkStream _ns;

        public string? ClientId { get; private set; }
        public string? Username { get; private set; }
        public string? PublicKey { get; private set; }

        public ClientConnection(TcpClient tcp, Server server)
        {
            _tcp = tcp;
            _server = server;
            _ns = tcp.GetStream();
        }

        public void Handle()
        {
            try
            {
                while (true)
                {
                    string? json = PacketIO.ReadJson(_ns);
                    if (json == null) break;

                    ProcessPacket(json);
                }
            }
            catch (IOException)
            {
                // Client closed connection normally → ignore
            }
            catch (ObjectDisposedException)
            {
                // Stream already disposed → ignore
            }
            catch (Exception ex)
            {
                Console.WriteLine("Client error: " + ex.Message);
            }

            finally
            {
                if (!string.IsNullOrEmpty(ClientId))
                    _server.Unregister(ClientId!);

                Close();
            }
        }

        private void ProcessPacket(string json)
        {
            PacketBase? basePkt;
            try { basePkt = JsonSerializer.Deserialize<PacketBase>(json); }
            catch { return; }

            if (basePkt == null) return;

            switch (basePkt.Type)
            {
                case PacketType.Register:
                    {
                        var pkt = JsonSerializer.Deserialize<RegisterPacket>(json);
                        if (pkt == null) return;

                        ClientId = pkt.ClientId;
                        Username = pkt.User;
                        PublicKey = pkt.PublicKey;

                        if (string.IsNullOrWhiteSpace(ClientId) ||
                            string.IsNullOrWhiteSpace(Username) ||
                            string.IsNullOrWhiteSpace(PublicKey))
                            return;

                        _server.Register(pkt.ClientId, pkt.User, pkt.PublicKey, this);
                        break;
                    }

                case PacketType.GetPublicKey:
                case PacketType.Chat:
                case PacketType.Typing:
                case PacketType.Logout:
                case PacketType.Recall:
                    _server.Route(json, this);
                    break;

                default:
                    break;
            }
        }

        public void SendPacket<T>(T packet)
        {
            try
            {
                PacketIO.SendPacket(_ns, packet);
            }
            catch { }
        }

        public void SendRaw(string json)
        {
            try
            {
                PacketIO.SendJson(_ns, json);
            }
            catch { }
        }

        public void Close()
        {
            try { _tcp.Close(); } catch { }
        }
    }
}
