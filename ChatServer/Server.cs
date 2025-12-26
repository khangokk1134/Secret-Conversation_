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
        private readonly TcpListener _listener;
        private readonly Thread _acceptThread;
        private bool _running;

        // ================= CORE STORAGE =================
        // clientId -> connection (CHỈ ONLINE)
        private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();

        // clientId -> publicKey (CHỈ ONLINE)
        private readonly ConcurrentDictionary<string, string> _pubKeys = new();

        // clientId -> username (KHÔNG BAO GIỜ XOÁ)
        private readonly ConcurrentDictionary<string, string> _userNames = new();

        private readonly string _offlineFolder = "offline";

        public Server(int port)
        {
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

        // ================= ACCEPT =================
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
                catch
                {
                    // ignore
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();
        }

        // ================= REGISTER =================
        public void Register(string clientId, string username, string pubKey, ClientConnection conn)
        {
            _clients[clientId] = conn;
            _pubKeys[clientId] = pubKey;

            // ⚠️ username chỉ set 1 lần
            _userNames.TryAdd(clientId, username);

            Console.WriteLine($"Client online: {username} ({clientId})");

            BroadcastUserList();
            DeliverOffline(clientId, conn);
        }

        // ================= UNREGISTER =================
        public void Unregister(string clientId)
        {
            // ❗ CHỈ XOÁ ONLINE CONNECTION
            _clients.TryRemove(clientId, out _);
            _pubKeys.TryRemove(clientId, out _);

            // ❌ KHÔNG XOÁ _userNames
            Console.WriteLine($"Client offline: {clientId}");

            BroadcastUserList();
        }

        public string? GetPublicKey(string clientId)
        {
            return _pubKeys.TryGetValue(clientId, out var k) ? k : null;
        }

        // ================= USER LIST =================
        private void BroadcastUserList()
        {
            var users = _userNames.Select(u => new
            {
                clientId = u.Key,
                user = u.Value,
                online = _clients.ContainsKey(u.Key)
            }).ToArray();

            var msg = new
            {
                type = "userlist",
                users
            };

            string json = JsonSerializer.Serialize(msg);

            foreach (var c in _clients.Values)
            {
                c.SendRaw(json);
            }
        }

        // ================= ROUTING =================
        public void Route(JsonElement root)
        {
            if (!root.TryGetProperty("type", out var t))
                return;

            string type = t.GetString() ?? "";

            switch (type)
            {
                case "chat":
                    RouteChat(root);
                    break;

                case "getpublickey":
                    RouteGetPublicKey(root);
                    break;
            }
        }

        // ================= CHAT =================
        private void RouteChat(JsonElement root)
        {
            string fromId = root.GetProperty("fromId").GetString()!;
            string toId = root.GetProperty("toId").GetString()!;

            // ===== ONLINE =====
            if (_clients.TryGetValue(toId, out var target))
            {
                target.SendRaw(root.GetRawText());

                if (_clients.TryGetValue(fromId, out var sender))
                {
                    sender.SendRaw(JsonSerializer.Serialize(new
                    {
                        type = "chat_ack",
                        toId,
                        status = "delivered"
                    }));
                }
            }
            // ===== OFFLINE =====
            else
            {
                StoreOffline(toId, root.GetRawText());

                if (_clients.TryGetValue(fromId, out var sender))
                {
                    sender.SendRaw(JsonSerializer.Serialize(new
                    {
                        type = "chat_ack",
                        toId,
                        status = "offline"
                    }));
                }
            }
        }

        // ================= PUBLIC KEY =================
        private void RouteGetPublicKey(JsonElement root)
        {
            string clientId = root.GetProperty("clientId").GetString()!;
            string requester = root.GetProperty("fromId").GetString()!;

            if (_pubKeys.TryGetValue(clientId, out var key) &&
                _clients.TryGetValue(requester, out var conn))
            {
                conn.SendRaw(JsonSerializer.Serialize(new
                {
                    type = "publickey",
                    clientId,
                    publicKey = key
                }));
            }
        }

        // ================= OFFLINE =================
        private void StoreOffline(string clientId, string json)
        {
            File.AppendAllText(
                Path.Combine(_offlineFolder, clientId + ".jsonl"),
                json + Environment.NewLine
            );
        }

        private void DeliverOffline(string clientId, ClientConnection conn)
        {
            string file = Path.Combine(_offlineFolder, clientId + ".jsonl");
            if (!File.Exists(file)) return;

            foreach (var line in File.ReadAllLines(file))
            {
                conn.SendRaw(line);
            }

            File.Delete(file);
        }
    }
}
