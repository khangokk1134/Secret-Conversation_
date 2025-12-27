using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Protocol;

namespace ChatServer
{
    public class Server
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly System.Threading.Thread _acceptThread;
        private bool _running;

        // ONLINE
        private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
        private readonly ConcurrentDictionary<string, string> _pubKeys = new();

        // NEVER delete
        private readonly ConcurrentDictionary<string, string> _userNames = new();

        private readonly string _offlineFolder = "offline";

        public Server(int port)
        {
            Directory.CreateDirectory(_offlineFolder);

            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            _listener.Start();

            _running = true;
            _acceptThread = new System.Threading.Thread(AcceptLoop) { IsBackground = true };
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
                    new System.Threading.Thread(conn.Handle) { IsBackground = true }.Start();
                }
                catch { }
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }

        public void Register(string clientId, string username, string pubKey, ClientConnection conn)
        {
            if (_clients.TryGetValue(clientId, out var oldConn))
            {
                try { oldConn.Close(); } catch { }
            }

            _clients[clientId] = conn;
            _pubKeys[clientId] = pubKey;
            _userNames.TryAdd(clientId, username);

            Console.WriteLine($"Client online: {username} ({clientId})");

            BroadcastUserList();
            DeliverOffline(clientId, conn);
        }

        public void Unregister(string clientId)
        {
            _clients.TryRemove(clientId, out _);
            _pubKeys.TryRemove(clientId, out _);

            Console.WriteLine($"Client offline: {clientId}");
            BroadcastUserList();
        }

        private void BroadcastUserList()
        {
            var users = _userNames.Select(u => new UserInfo
            {
                ClientId = u.Key,
                User = u.Value,
                Online = _clients.ContainsKey(u.Key)
            }).ToArray();

            var pkt = new UserListPacket { Users = users };

            foreach (var c in _clients.Values)
                c.SendPacket(pkt);
        }

        public void Route(string json, ClientConnection senderConn)
        {
            PacketBase? basePkt;
            try { basePkt = JsonSerializer.Deserialize<PacketBase>(json); }
            catch { return; }
            if (basePkt == null) return;

            switch (basePkt.Type)
            {
                case PacketType.Chat:
                    RouteChat(json);
                    break;

                case PacketType.Typing:
                    RouteTyping(json);
                    break;

                case PacketType.GetPublicKey:
                    RouteGetPublicKey(json, senderConn);
                    break;

                case PacketType.Logout:
                    RouteLogout(senderConn);
                    break;

                case PacketType.Recall:
                    RouteRecall(json);
                    break;
            }
        }

        private void RouteChat(string json)
        {
            ChatPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<ChatPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.FromId) || string.IsNullOrEmpty(pkt.ToId))
                return;

            if (_clients.TryGetValue(pkt.ToId, out var target))
            {
                target.SendRaw(json);

                if (_clients.TryGetValue(pkt.FromId, out var sender))
                {
                    sender.SendPacket(new ChatAckPacket
                    {
                        MessageId = pkt.MessageId,
                        FromId = pkt.FromId,
                        ToId = pkt.ToId,
                        Status = "delivered"
                    });
                }
            }
            else
            {
                StoreOffline(pkt.ToId, json);

                if (_clients.TryGetValue(pkt.FromId, out var sender))
                {
                    sender.SendPacket(new ChatAckPacket
                    {
                        MessageId = pkt.MessageId,
                        FromId = pkt.FromId,
                        ToId = pkt.ToId,
                        Status = "offline"
                    });
                }
            }
        }

        private void RouteTyping(string json)
        {
            TypingPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<TypingPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.ToId))
                return;

            if (_clients.TryGetValue(pkt.ToId, out var target))
                target.SendRaw(json);
        }

        private void RouteGetPublicKey(string json, ClientConnection requesterConn)
        {
            GetPublicKeyPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<GetPublicKeyPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.ClientId))
                return;

            if (_pubKeys.TryGetValue(pkt.ClientId, out var key))
            {
                requesterConn.SendPacket(new PublicKeyPacket
                {
                    ClientId = pkt.ClientId,
                    PublicKey = key
                });
            }
        }

        private void RouteLogout(ClientConnection senderConn)
        {
            if (!string.IsNullOrEmpty(senderConn.ClientId))
                Unregister(senderConn.ClientId!);

            try { senderConn.Close(); } catch { }
        }

        private void RouteRecall(string json)
        {
            RecallPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<RecallPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (!string.IsNullOrEmpty(pkt.ToId) && _clients.TryGetValue(pkt.ToId, out var target))
                target.SendRaw(json);
        }

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
                if (string.IsNullOrWhiteSpace(line)) continue;

                // đảm bảo gửi JSON sạch, không \n dư
                var json = line.Trim();
                conn.SendRaw(json);
            }

            try { File.Delete(file); } catch { }
        }
    }
}
