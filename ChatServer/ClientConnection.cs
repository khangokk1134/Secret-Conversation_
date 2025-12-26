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

        // ===== IDENTIFICATION =====
        public string? ClientId { get; private set; }
        public string? Username { get; private set; }
        public string? PublicKey { get; private set; }

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

        // ================= MAIN LOOP =================
        public void Handle()
        {
            try
            {
                while (true)
                {
                    string? json = _reader.ReadLine();
                    if (json == null)
                        break;

                    ProcessPacket(json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Client error: " + ex.Message);
            }
            finally
            {
                if (ClientId != null)
                    _server.Unregister(ClientId);

                Close();
            }
        }

        // ================= PACKET PROCESS =================
        private void ProcessPacket(string json)
        {
            JsonElement root;

            try
            {
                root = JsonDocument.Parse(json).RootElement;
            }
            catch
            {
                Console.WriteLine("Invalid JSON from client");
                return;
            }

            if (!root.TryGetProperty("type", out var tProp))
                return;

            string? type = tProp.GetString();

            switch (type)
            {
                // ===== REGISTER =====
                case "register":
                    {
                        ClientId = root.GetProperty("clientId").GetString();
                        Username = root.GetProperty("user").GetString();
                        PublicKey = root.GetProperty("publicKey").GetString();

                        if (ClientId == null || Username == null || PublicKey == null)
                            return;

                        _server.Register(ClientId, Username, PublicKey, this);
                        break;
                    }

                // ===== GET PUBLIC KEY =====
                case "getpublickey":
                    {
                        var reqId = root.GetProperty("clientId").GetString();
                        if (string.IsNullOrEmpty(reqId))
                            return;

                        var pk = _server.GetPublicKey(reqId);

                        // ❗ KHÔNG gửi nếu chưa có key
                        if (pk == null)
                        {
                            Console.WriteLine($"Public key not found for {reqId}");
                            return;
                        }

                        SendRaw(JsonSerializer.Serialize(new
                        {
                            type = "publickey",
                            clientId = reqId,
                            publicKey = pk
                        }));
                        break;
                    }

                // ===== CHAT / TYPING =====
                case "chat":
                case "typing":
                    _server.Route(root);
                    break;
            }
        }

        // ================= SEND =================
        public void SendRaw(string json)
        {
            try
            {
                _writer.WriteLine(json);
            }
            catch
            {
                // ignore broken connection
            }
        }

        // ================= CLOSE =================
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
