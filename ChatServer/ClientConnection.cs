using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChatServer
{
    public class ClientConnection
    {
        private readonly TcpClient _tcp;
        private readonly Server _server;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public string? Username { get; set; }
        public string? PublicKeyXml { get; set; }

        public ClientConnection(TcpClient tcp, Server server)
        {
            _tcp = tcp;
            _server = server;

            var ns = tcp.GetStream();
            _reader = new StreamReader(ns, Encoding.UTF8);
            _writer = new StreamWriter(ns, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        public void Handle()
        {
            try
            {
                while (true)
                {
                    string? line = _reader.ReadLine();
                    if (line == null)
                        break;

                    ProcessPacket(line);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Client error: " + ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(Username))
                    _server.Unregister(Username);

                Close();
            }
        }

        // =========================
        // PACKET HANDLING
        // =========================
        private void ProcessPacket(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch
            {
                Console.WriteLine("Invalid JSON received");
                return;
            }

            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            string type = typeProp.GetString();

            switch (type)
            {
                case "register":
                    HandleRegister(root);
                    break;

                case "get_pubkey":
                    HandleGetPublicKey(root);
                    break;

                case "chat":
                case "typing":
                case "recall":
                    _server.RoutePacket(root);
                    break;

                default:
                    Console.WriteLine($"Unknown packet type: {type}");
                    break;
            }
        }

        // =========================
        // HANDLERS
        // =========================
        private void HandleRegister(JsonElement root)
        {
            string? user = root.GetProperty("user").GetString();
            string? pub = root.GetProperty("pubkey").GetString();

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pub))
                return;

            Username = user;
            PublicKeyXml = pub;

            _server.Register(user, this, pub);
        }

        private void HandleGetPublicKey(JsonElement root)
        {
            string? user = root.GetProperty("user").GetString();
            if (string.IsNullOrEmpty(user))
                return;

            var pk = _server.GetPublicKey(user);

            var resp = new
            {
                type = "pubkey",
                user = user,
                pubkey = pk
            };

            SendRaw(JsonSerializer.Serialize(resp));
        }

        // =========================
        // SEND / CLOSE
        // =========================
        public void SendRaw(string json)
        {
            try
            {
                _writer.WriteLine(json);
            }
            catch { }
        }

        public void Close()
        {
            try
            {
                _tcp.Close();
            }
            catch { }
        }
    }
}
