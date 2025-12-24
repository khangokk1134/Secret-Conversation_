using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace ChatServer
{
    public class Server
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private bool _running;

        // username -> connection
        private ConcurrentDictionary<string, ClientConnection> _clients = new();

        private readonly string _offlineFolder = "offline";

        public Server(int port)
        {
            if (!Directory.Exists(_offlineFolder))
                Directory.CreateDirectory(_offlineFolder);

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            _running = true;
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true
            };
            _acceptThread.Start();

            Console.WriteLine($"Server started on port {port}");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    var conn = new ClientConnection(tcp, this);
                    new Thread(conn.Handle)
                    {
                        IsBackground = true
                    }.Start();
                }
                catch (SocketException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("AcceptLoop error: " + ex.Message);
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();

            foreach (var c in _clients.Values)
                c.Close();

            Console.WriteLine("Server stopped.");
        }

        // =========================
        // USER MANAGEMENT
        // =========================

        public void Register(string username, ClientConnection connection, string publicKeyXml)
        {
            connection.Username = username;
            connection.PublicKeyXml = publicKeyXml;

            _clients[username] = connection;
            Console.WriteLine($"User online: {username}");

            BroadcastUserList();
            DeliverOfflineMessages(username, connection);
        }

        public void Unregister(string username)
        {
            if (username == null) return;

            _clients.TryRemove(username, out _);
            Console.WriteLine($"User offline: {username}");

            BroadcastUserList();
        }

        public string GetPublicKey(string username)
        {
            return _clients.TryGetValue(username, out var c)
                ? c.PublicKeyXml
                : null;
        }

        private void BroadcastUserList()
        {
            var msg = new
            {
                type = "userlist",
                users = _clients.Keys.ToArray()
            };

            string json = JsonSerializer.Serialize(msg);

            foreach (var c in _clients.Values)
            {
                try { c.SendRaw(json); } catch { }
            }
        }

        // =========================
        // MESSAGE ROUTING
        // =========================

        public void RoutePacket(JsonElement packet)
        {
            string type = packet.GetProperty("type").GetString();
            string to = packet.TryGetProperty("to", out var t) ? t.GetString() : null;

            // Typing indicator: only route, no offline store
            if (type == "typing")
            {
                if (_clients.TryGetValue(to, out var dest))
                    dest.SendRaw(packet.GetRawText());
                return;
            }

            // Chat / Recall / Others
            if (_clients.TryGetValue(to, out var target))
            {
                target.SendRaw(packet.GetRawText());
                Console.WriteLine($"Routed {type} to {to}");
            }
            else
            {
                StoreOffline(to, packet.GetRawText());
                Console.WriteLine($"Stored offline {type} for {to}");
            }
        }

        // =========================
        // OFFLINE MESSAGE
        // =========================

        private void StoreOffline(string username, string json)
        {
            if (string.IsNullOrEmpty(username)) return;

            string file = Path.Combine(_offlineFolder, username + ".jsonl");
            File.AppendAllText(file, json + Environment.NewLine);
        }

        private void DeliverOfflineMessages(string username, ClientConnection connection)
        {
            string file = Path.Combine(_offlineFolder, username + ".jsonl");
            if (!File.Exists(file)) return;

            var lines = File.ReadAllLines(file);
            foreach (var l in lines)
                connection.SendRaw(l);

            File.Delete(file);
            Console.WriteLine($"Delivered {lines.Length} offline messages to {username}");
        }
    }
}
