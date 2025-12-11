using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChatServer
{
    public class ClientConnection
    {
        private TcpClient _tcp;
        private Server _server;
        private StreamReader _reader;
        private StreamWriter _writer;
        public string Username { get; set; }
        public string PublicKeyXml { get; set; }

        public ClientConnection(TcpClient tcp, Server server)
        {
            _tcp = tcp;
            _server = server;
            var ns = tcp.GetStream();
            _reader = new StreamReader(ns, Encoding.UTF8);
            _writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
        }

        public void Handle()
        {
            try
            {
                while (true)
                {
                    string line = _reader.ReadLine();
                    if (line == null) break;

                    try
                    {
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString();

                        if (type == "register")
                        {
                            string user = root.GetProperty("user").GetString();
                            string pub = root.GetProperty("pubkey").GetString();
                            _server.Register(user, this, pub);
                        }
                        else if (type == "get_pubkey")
                        {
                            string user = root.GetProperty("user").GetString();
                            var pk = _server.GetPublicKey(user);
                            var resp = new { type = "pubkey", user = user, pubkey = pk };
                            SendRaw(JsonSerializer.Serialize(resp));
                        }
                        else if (type == "chat")
                        {
                            // route to recipient
                            string to = root.GetProperty("to").GetString();
                            _server.RouteMessage(to, line);
                        }
                        else
                        {
                            // unknown -> ignore
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Parse error: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                // connection closed
                Console.WriteLine("Client disconnected: " + ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(Username))
                    _server.Unregister(Username);
                Close();
            }
        }

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
            try { _tcp.Close(); } catch { }
        }
    }
}
