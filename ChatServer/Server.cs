using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

       
        private readonly ConcurrentDictionary<string, object> _offlineLocks = new();
        private object OfflineLock(string clientId) => _offlineLocks.GetOrAdd(clientId, _ => new object());


        private void BroadcastToRoom(string roomId, object pkt, string? exceptClientId = null)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return;

            foreach (var mid in room.Members)
            {
                if (!string.IsNullOrEmpty(exceptClientId) && mid == exceptClientId)
                    continue;

                SendPacketToClient(mid, pkt);
            }
        }

       
        private sealed class RoomState
        {
            public string RoomId = "";
            public string RoomName = "";
            public HashSet<string> Members = new HashSet<string>(StringComparer.Ordinal);
        }

        private readonly ConcurrentDictionary<string, RoomState> _rooms = new();

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

           
            BroadcastRoomsTo(clientId);

            
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

        private void SendPacketToClient<T>(string clientId, T pkt)
        {
            if (string.IsNullOrEmpty(clientId)) return;
            if (_clients.TryGetValue(clientId, out var c))
                c.SendPacket(pkt);
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

                case PacketType.SeenReceipt:
                    RouteSeenReceipt(json);
                    break;

                case PacketType.FileOffer:
                    RouteFileOffer(json);
                    break;

                case PacketType.FileChunk:
                    RouteFileChunk(json);
                    break;

                case PacketType.FileComplete:
                    RouteFileComplete(json);
                    break;

                
                case PacketType.CreateRoom:
                    RouteCreateRoom(json);
                    break;

                case PacketType.RoomChat:
                    RouteRoomChat(json);
                    break;

                case PacketType.RoomDeliveryReceipt:
                    RouteRoomDeliveryReceipt(json);
                    break;

                case PacketType.LeaveRoom:
                    RouteLeaveRoom(json);
                    break;

                case PacketType.RoomFileOffer:
                    RouteRoomFileOffer(json);
                    break;

                case PacketType.FileAccept:
                    RouteFileAccept(json);
                    break;

                case PacketType.RoomFileAccept: RouteRoomFileAccept(json); break;
            }
        }


        private void RouteRoomFileOffer(string json)
        {
            RoomFileOfferPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<RoomFileOfferPacket>(json); } catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.RoomId) || string.IsNullOrEmpty(pkt.FileId) || string.IsNullOrEmpty(pkt.FromId))
                return;

            if (!_rooms.TryGetValue(pkt.RoomId, out var room)) return;
            if (!room.Members.Contains(pkt.FromId)) return;

            foreach (var mid in room.Members)
            {
                if (mid == pkt.FromId) continue;

                if (_clients.TryGetValue(mid, out var c))
                    c.SendRaw(json);
                else
                    StoreOffline(mid, json);
            }
        }

        private void RouteRoomFileAccept(string json)
        {
            RoomFileAcceptPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<RoomFileAcceptPacket>(json); } catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.RoomId) ||
                string.IsNullOrEmpty(pkt.FileId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId))
                return;

            SendPacketToClient(pkt.ToId, pkt);
        }


       
        private void RouteCreateRoom(string json)
        {
            CreateRoomPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<CreateRoomPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.RoomId) ||
                string.IsNullOrEmpty(pkt.RoomName) ||
                string.IsNullOrEmpty(pkt.CreatorId))
                return;

            var members = new HashSet<string>(pkt.MemberIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            members.Add(pkt.CreatorId);

            var room = new RoomState
            {
                RoomId = pkt.RoomId,
                RoomName = pkt.RoomName,
                Members = members
            };

            _rooms[pkt.RoomId] = room;

            Console.WriteLine($"Room created: {pkt.RoomName} ({pkt.RoomId}) members={members.Count}");

           
            var info = new RoomInfoPacket
            {
                RoomId = room.RoomId,
                RoomName = room.RoomName,
                MemberIds = room.Members.ToArray()
            };

            foreach (var mid in room.Members)
                SendPacketToClient(mid, info);
        }

        private void BroadcastRoomsTo(string clientId)
        {
            foreach (var r in _rooms.Values)
            {
                if (!r.Members.Contains(clientId)) continue;

                SendPacketToClient(clientId, new RoomInfoPacket
                {
                    RoomId = r.RoomId,
                    RoomName = r.RoomName,
                    MemberIds = r.Members.ToArray()
                });
            }
        }

        private void RouteFileOffer(string json)
        {
            FileOfferPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<FileOfferPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.FileId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId) ||
                string.IsNullOrEmpty(pkt.FileName) ||
                pkt.FileSize < 0)
                return;

            if (_clients.TryGetValue(pkt.ToId, out var target))
            {
                target.SendRaw(json);
            }
            else
            {
              
                StoreOffline(pkt.ToId, json);
            }
        }

        private void RouteFileChunk(string json)
        {
            FileChunkPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<FileChunkPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.FileId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId) ||
                pkt.Data == null)
                return;

            if (_clients.TryGetValue(pkt.ToId, out var target))
            {
                target.SendRaw(json);
            }
            else
            {
                
            }
        }

        private void RouteFileAccept(string json)
        {
            FileAcceptPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<FileAcceptPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.FileId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId))
                return;

            if (_clients.TryGetValue(pkt.ToId, out var sender))
            {
                sender.SendRaw(json);
            }
            else
            {
                
            }
        }

        private void RouteFileComplete(string json)
        {
            FileCompletePacket? pkt;
            try { pkt = JsonSerializer.Deserialize<FileCompletePacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.FileId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId))
                return;

            if (_clients.TryGetValue(pkt.ToId, out var target))
            {
                target.SendRaw(json);
            }
            else
            {
                StoreOffline(pkt.ToId, json);
            }
        }

       
        private void RouteRoomChat(string json)
        {
            RoomChatPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<RoomChatPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.RoomId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.MessageId) ||
                string.IsNullOrEmpty(pkt.EncMsg) ||
                pkt.EncKeys == null || pkt.EncKeys.Count == 0)
                return;

            if (!_rooms.TryGetValue(pkt.RoomId, out var room))
                return;

            if (!room.Members.Contains(pkt.FromId))
                return;

           
            var idemKey = $"room:{pkt.RoomId}:{pkt.MessageId}";
            if (!_seenMessages.TryAdd(idemKey, 1))
                return;

           
            SendPacketToClient(pkt.FromId, new RoomAckPacket
            {
                RoomId = pkt.RoomId,
                MessageId = pkt.MessageId,
                FromId = pkt.FromId,
                Status = "accepted"
            });

            int forwarded = 0;
            int storedOffline = 0;

            foreach (var memberId in room.Members)
            {
                if (memberId == pkt.FromId) continue;

                if (_clients.TryGetValue(memberId, out var target))
                {
                    try
                    {
                        target.SendRaw(json);
                        forwarded++;
                    }
                    catch
                    {
                        
                        try { target.Close(); } catch { }
                        _clients.TryRemove(memberId, out _);
                        StoreOffline(memberId, json);
                        storedOffline++;
                    }
                }
                else
                {
                    StoreOffline(memberId, json);
                    storedOffline++;
                }
            }

            
            SendPacketToClient(pkt.FromId, new RoomAckPacket
            {
                RoomId = pkt.RoomId,
                MessageId = pkt.MessageId,
                FromId = pkt.FromId,
                Status = storedOffline > 0 ? "offline_saved" : "delivered"
            });
        }

        
        private void RouteRoomDeliveryReceipt(string json)
        {
            RoomDeliveryReceiptPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<RoomDeliveryReceiptPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.RoomId) ||
                string.IsNullOrEmpty(pkt.MessageId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId))
                return;

            if (pkt.Status != "delivered_to_client")
                return;

          
            RemoveOfflineRoomMessage(pkt.FromId, pkt.RoomId, pkt.MessageId);

            
            SendPacketToClient(pkt.ToId, new RoomAckPacket
            {
                RoomId = pkt.RoomId,
                MessageId = pkt.MessageId,
                FromId = pkt.ToId,
                Status = "delivered_to_client"
            });

            
            var idemKey = $"room:{pkt.RoomId}:{pkt.MessageId}";
            _seenMessages.TryRemove(idemKey, out _);
        }

        
        private void RouteLeaveRoom(string json)
        {
            LeaveRoomPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<LeaveRoomPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.RoomId) || string.IsNullOrEmpty(pkt.ClientId))
                return;

            if (!_rooms.TryGetValue(pkt.RoomId, out var room))
                return;

            if (!room.Members.Contains(pkt.ClientId))
                return;

            room.Members.Remove(pkt.ClientId);

            
            SendPacketToClient(pkt.ClientId, new RoomInfoRemovedPacket
            {
                RoomId = pkt.RoomId
            });

            
            if (room.Members.Count == 0)
            {
                _rooms.TryRemove(pkt.RoomId, out _);
                return;
            }

            
            var info = new RoomInfoPacket
            {
                RoomId = room.RoomId,
                RoomName = room.RoomName,
                MemberIds = room.Members.ToArray()
            };

            foreach (var mid in room.Members)
                SendPacketToClient(mid, info);
        }

       
        private void RouteSeenReceipt(string json)
        {
            SeenReceiptPacket? pkt;
            try { pkt = JsonSerializer.Deserialize<SeenReceiptPacket>(json); }
            catch { return; }
            if (pkt == null) return;

            if (string.IsNullOrEmpty(pkt.MessageId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.ToId))
                return;

            var seenKey = $"seen:{pkt.FromId}:{pkt.ToId}:{pkt.MessageId}";
            if (!_seenMessages.TryAdd(seenKey, 1))
                return;

            SendPacketToClient(pkt.ToId, new ChatAckPacket
            {
                MessageId = pkt.MessageId,
                FromId = pkt.ToId,
                ToId = pkt.FromId,
                Status = "seen"
            });
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

            var idemKey = $"{pkt.FromId}:{pkt.ToId}:{pkt.MessageId}";
            if (!_seenMessages.TryAdd(idemKey, 1))
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

           
            if (pkt.Status != "delivered_to_client")
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
            try
            {
                lock (OfflineLock(clientId))
                {
                    File.AppendAllText(OfflineFile(clientId), json + Environment.NewLine);
                }
            }
            catch { }
        }

        private void DeliverOffline(string clientId, ClientConnection conn)
        {
            var file = OfflineFile(clientId);
            if (!File.Exists(file)) return;

            string[] lines;
            try
            {
                lock (OfflineLock(clientId))
                {
                   
                    lines = File.ReadAllLines(file);
                }
            }
            catch { return; }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                conn.SendRaw(line.Trim());
            }
        }

      
        private void RemoveOfflineMessage(string receiverId, string messageId)
        {
            var file = OfflineFile(receiverId);
            if (!File.Exists(file)) return;

            lock (OfflineLock(receiverId))
            {
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

                        ChatPacket? pkt = null;
                        try { pkt = JsonSerializer.Deserialize<ChatPacket>(raw); } catch { }

                        if (pkt != null && pkt.MessageId == messageId)
                            remove = true;

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

        
        private void RemoveOfflineRoomMessage(string receiverId, string roomId, string messageId)
        {
            var file = OfflineFile(receiverId);
            if (!File.Exists(file)) return;

            lock (OfflineLock(receiverId))
            {
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

                        PacketBase? basePkt = null;
                        try { basePkt = JsonSerializer.Deserialize<PacketBase>(raw); } catch { }

                        if (basePkt != null && basePkt.Type == PacketType.RoomChat)
                        {
                            RoomChatPacket? rp = null;
                            try { rp = JsonSerializer.Deserialize<RoomChatPacket>(raw); } catch { }

                            if (rp != null && rp.RoomId == roomId && rp.MessageId == messageId)
                                remove = true;
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
}
