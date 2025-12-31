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

        private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
        private readonly ConcurrentDictionary<string, byte> _seenMessages = new();

        private readonly ConcurrentDictionary<string, string> _pubKeys = new();
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
            _userNames[clientId] = username;

            Console.WriteLine($"Client online: {username} ({clientId})");

            BroadcastUserList();
            DeliverOffline(clientId, conn);
        }

        public void Unregister(string clientId)
        {
            _clients.TryRemove(clientId, out _);
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
                case PacketType.Register:
                    RouteRegister(json, senderConn);
                    break;

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

                case PacketType.DeliveryReceipt:
                    RouteDeliveryReceipt(json);
                    break;
            }
        }

        private void RouteRegister(string json, ClientConnection conn)
        {
            RegisterPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<RegisterPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.ClientId) ||
                string.IsNullOrEmpty(pkt.User) ||
                string.IsNullOrEmpty(pkt.PublicKey))
                return;

            conn.SetIdentity(pkt.ClientId, pkt.User, pkt.PublicKey);
            Register(pkt.ClientId, pkt.User, pkt.PublicKey, conn);
        }

        private void RouteChat(string json)
        {
            ChatPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<ChatPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.MessageId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId))
                return;

            var seenKey = $"{pkt.FromId}:{pkt.ToId}:{pkt.MessageId}";
            if (!_seenMessages.TryAdd(seenKey, 1))
                return;

            if (_clients.TryGetValue(pkt.FromId, out var sender0))
            {
                sender0.SendPacket(new ChatAckPacket
                {
                    MessageId = pkt.MessageId,
                    FromId = pkt.FromId,
                    ToId = pkt.ToId,
                    Status = "accepted"
                });
            }

            if (_clients.TryGetValue(pkt.ToId, out var target))
            {
                target.SendRaw(json);

                if (_clients.TryGetValue(pkt.FromId, out var sender1))
                {
                    sender1.SendPacket(new ChatAckPacket
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

                if (_clients.TryGetValue(pkt.FromId, out var sender2))
                {
                    sender2.SendPacket(new ChatAckPacket
                    {
                        MessageId = pkt.MessageId,
                        FromId = pkt.FromId,
                        ToId = pkt.ToId,
                        Status = "offline_saved"
                    });
                }
            }
        }

        private void RouteDeliveryReceipt(string json)
        {
            DeliveryReceiptPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<DeliveryReceiptPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.MessageId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId) ||
                string.IsNullOrEmpty(pkt.Status))
                return;

            if (pkt.Status != "received")
                return;

            RemoveOfflineMessage(pkt.FromId, pkt.MessageId);

            if (_clients.TryGetValue(pkt.ToId, out var sender))
            {
                sender.SendPacket(new ChatAckPacket
                {
                    MessageId = pkt.MessageId,
                    FromId = pkt.ToId,
                    ToId = pkt.FromId,
                    Status = "delivered_to_client"
                });
            }

            var idemKey = $"{pkt.ToId}:{pkt.FromId}:{pkt.MessageId}";
            _seenMessages.TryRemove(idemKey, out _);
        }

        private void RouteTyping(string json)
        {
            TypingPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<TypingPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.ToId)) return;

            if (_clients.TryGetValue(pkt.ToId, out var target))
                target.SendRaw(json);
        }

        private void RouteGetPublicKey(string json, ClientConnection requesterConn)
        {
            GetPublicKeyPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<GetPublicKeyPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.ClientId)) return;

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

        private string OfflineFile(string clientId) => Path.Combine(_offlineFolder, clientId + ".jsonl");

        private void StoreOffline(string clientId, string json)
        {
            File.AppendAllText(OfflineFile(clientId), json + Environment.NewLine);
        }

        private void DeliverOffline(string clientId, ClientConnection conn)
        {
            var file = OfflineFile(clientId);
            if (!File.Exists(file)) return;

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                conn.SendRaw(line.Trim());
            }
        }

        private void RemoveOfflineMessage(string receiverId, string messageId)
        {
            var file = OfflineFile(receiverId);
            if (!File.Exists(file)) return;

            try
            {
                var temp = file + ".tmp";
                using var w = new StreamWriter(temp);

                int kept = 0;
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var raw = line.Trim();
                    bool remove = false;

                    try
                    {
                        var pkt = JsonSerializer.Deserialize<ChatPacket>(raw);
                        if (pkt?.MessageId == messageId) remove = true;
                    }
                    catch
                    {
                        remove = false;
                    }

                    if (remove) continue;

                    w.WriteLine(raw);
                    kept++;
                }

                w.Flush();
                w.Close();

                if (kept == 0)
                {
                    try { File.Delete(file); } catch { }
                    try { File.Delete(temp); } catch { }
                }
                else
                {
                    File.Copy(temp, file, true);
                    try { File.Delete(temp); } catch { }
                }
            }
            catch { }
        }
    }
}
