using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Protocol;

namespace ChatClient
{
    public partial class RemoteClientForm : Form
    {
        TcpClient? _tcp;
        NetworkStream? _ns;
        Thread? _recvThread;

        volatile bool _closing = false;
        volatile bool _connected = false;

        
        volatile bool _manualDisconnect = false;

        
        CancellationTokenSource? _reconnectCts;
        int _reconnectRunning = 0;

       
        string _serverIp = "";
        int _serverPort = 5000;

        string _clientId = "";
        string? _username;

        string _pubXml = "", _privXml = "";

        readonly Dictionary<string, string> _pubCache = new();
        readonly Dictionary<string, TaskCompletionSource<string>> _pubWaiters = new();
        readonly object _pubLock = new();

        
        readonly Dictionary<string, string> _userNames = new(); 
        readonly Dictionary<string, bool> _userOnline = new();  
        string? _activePeerId = null;
        string? _activePeerUser = null;

         
        readonly Dictionary<string, int> _unread = new();                
        readonly Dictionary<string, (long ts, string preview)> _lastMsg = new(); 
        readonly object _convLock = new();

     
        readonly Dictionary<string, string> _sentMessagePreview = new(); 
        readonly Dictionary<string, string> _msgStatus = new();          
        readonly object _statusLock = new();

        
        readonly Dictionary<string, int> _statusTokenPos = new();
        readonly object _statusTokenLock = new();

        
        readonly HashSet<string> _seenIncoming = new();
        readonly object _seenLock = new();

       
        class PendingMsg
        {
            public ChatPacket Packet = new ChatPacket();
            public int Attempts = 0;
            public long FirstSentMs;
            public long LastSentMs;
            public string Stage = "new"; 
        }

        readonly Dictionary<string, PendingMsg> _pending = new();
        readonly object _pendingLock = new();

        System.Windows.Forms.Timer _resendTimer;

        
        System.Windows.Forms.Timer _typingUiClearTimer;   
        System.Windows.Forms.Timer _typingSendDebounce;   
        bool _typingSent = false;
        long _lastTypedAtMs = 0;
        string? _typingUser; 

        public RemoteClientForm()
        {
            InitializeComponent();

            Directory.CreateDirectory("keys");

            var pubPath = Path.Combine("keys", "my_pub.xml");
            var privPath = Path.Combine("keys", "my_priv.xml");

            if (File.Exists(pubPath) && File.Exists(privPath))
            {
                _pubXml = File.ReadAllText(pubPath);
                _privXml = File.ReadAllText(privPath);
            }
            else
            {
                CryptoHelper.GenerateRsaKeys(out _pubXml, out _privXml);
                File.WriteAllText(pubPath, _pubXml);
                File.WriteAllText(privPath, _privXml);
            }

          
            _typingUiClearTimer = new System.Windows.Forms.Timer();
            _typingUiClearTimer.Interval = 1500;
            _typingUiClearTimer.Tick += (s, e) =>
            {
                ClearTyping();
                _typingUiClearTimer.Stop();
            };

           
            _typingSendDebounce = new System.Windows.Forms.Timer();
            _typingSendDebounce.Interval = 350; 
            _typingSendDebounce.Tick += (s, e) =>
            {
                _typingSendDebounce.Stop();
                TrySendTypingStart();
            };

            txtMessage.TextChanged += TxtMessage_TextChanged;

            lstUsers.SelectedIndexChanged += (s, e) =>
            {
                if (lstUsers.SelectedItem == null) return;

                var display = lstUsers.SelectedItem.ToString()!;
                var peerId = GetClientIdByDisplay(display);
                if (peerId == null) return;

              
                if (peerId == _clientId)
                {
                    UI(() => lstUsers.ClearSelected());
                    return;
                }

                var peerUser = ExtractNameFromDisplay(display);
                SwitchConversation(peerId, peerUser);
            };

            _resendTimer = new System.Windows.Forms.Timer();
            _resendTimer.Interval = 1000;
            _resendTimer.Tick += (s, e) => ResendTick();
            _resendTimer.Stop();

            SetUiConnected(false);
            this.FormClosing += RemoteClientForm_FormClosing;
        }

        
        private void btnConnect_Click(object sender, EventArgs e)
        {
            if (IsReallyConnected())
            {
                Disconnect("user");
                return;
            }

            _manualDisconnect = false;
            StopReconnectLoop();

            _connected = false;
            InternalDisconnect("reset before connect", sendLogout: false);

            UI(() =>
            {
                lstUsers.Items.Clear();
                _userNames.Clear();
                _userOnline.Clear();
            });

            _username = txtUser.Text.Trim();
            if (string.IsNullOrEmpty(_username)) return;

            _serverIp = txtServerIP.Text.Trim();
            if (!int.TryParse(txtPort.Text, out _serverPort)) return;

            _clientId = LoadOrCreateClientId(_username);

           
            RebuildConversationSummariesFromDisk();

            _closing = false;

            _ = Task.Run(async () =>
            {
                var ok = await TryConnectOnceAsync(CancellationToken.None);
                if (!ok && !_closing && !_manualDisconnect)
                {
                    StartReconnectLoop("connect failed");
                }
            });
        }

        private void Disconnect(string reason)
        {
            if (_closing) return;

            _manualDisconnect = true;
            StopReconnectLoop();

            InternalDisconnect(reason, sendLogout: true);

            UI(() =>
            {
                lstUsers.Items.Clear();
                _userNames.Clear();
                _userOnline.Clear();
            });

            UI(() => rtbChat.AppendText($"[Disconnected: {reason}]\n"));
        }

        private void InternalDisconnect(string reason, bool sendLogout)
        {
            _connected = false;

            try { _resendTimer.Stop(); } catch { }
            try { _typingUiClearTimer.Stop(); } catch { }
            try { _typingSendDebounce.Stop(); } catch { }

          
            _typingSent = false;

            lock (_pubLock)
            {
                foreach (var kv in _pubWaiters)
                    kv.Value.TrySetCanceled();
                _pubWaiters.Clear();
            }

            try { ClearTyping(); } catch { }

            if (sendLogout)
            {
                try
                {
                    if (_ns != null && !string.IsNullOrEmpty(_clientId))
                        SendPacket(new LogoutPacket { ClientId = _clientId });
                }
                catch { }
            }

            try { _ns?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }

            _ns = null;
            _tcp = null;

            SetUiConnected(false);
        }

        private void InternalDisconnect(string reason) => InternalDisconnect(reason, sendLogout: true);

        
        private void StartReconnectLoop(string reason)
        {
            if (_closing) return;
            if (_manualDisconnect) return;

            if (Interlocked.Exchange(ref _reconnectRunning, 1) == 1)
                return;

            StopReconnectLoop();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            UI(() => rtbChat.AppendText($"[Auto-reconnect started: {reason}]\n"));

            _ = Task.Run(async () =>
            {
                int[] delays = new[] { 1000, 2000, 5000, 10000, 20000 };
                int idx = 0;

                try
                {
                    while (!token.IsCancellationRequested && !_closing && !_manualDisconnect)
                    {
                        if (IsReallyConnected())
                            break;

                        int delay = delays[Math.Min(idx, delays.Length - 1)];
                        if (idx < delays.Length - 1) idx++;

                        UI(() => rtbChat.AppendText($"[Reconnecting in {delay / 1000.0:0.#}s...]\n"));
                        try { await Task.Delay(delay, token); }
                        catch { break; }

                        if (token.IsCancellationRequested || _closing || _manualDisconnect)
                            break;

                        InternalDisconnect("reconnect cleanup", sendLogout: false);

                        var ok = await TryConnectOnceAsync(token);
                        if (ok)
                        {
                            UI(() => rtbChat.AppendText("[Reconnected]\n"));
                            break;
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectRunning, 0);
                }
            }, token);
        }

        private void StopReconnectLoop()
        {
            try
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();
            }
            catch { }
            _reconnectCts = null;
            Interlocked.Exchange(ref _reconnectRunning, 0);
        }

        private async Task<bool> TryConnectOnceAsync(CancellationToken token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_serverIp) || _serverPort <= 0)
                    return false;
                if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_clientId))
                    return false;

                var tcp = new TcpClient();

                var connectTask = tcp.ConnectAsync(_serverIp, _serverPort);
                var done = await Task.WhenAny(connectTask, Task.Delay(8000, token));
                if (done != connectTask)
                {
                    try { tcp.Close(); } catch { }
                    return false;
                }

                await connectTask;

                var ns = tcp.GetStream();

                _tcp = tcp;
                _ns = ns;

                _connected = true;
                SetUiConnected(true);

                
                SendPacket(new RegisterPacket
                {
                    ClientId = _clientId,
                    User = _username!,
                    PublicKey = _pubXml
                });

                _recvThread = new Thread(ReceiveLoop) { IsBackground = true };
                _recvThread.Start();

                _resendTimer.Start();

                UI(() => rtbChat.AppendText("[Connected]\n"));
                return true;
            }
            catch (Exception ex)
            {
                UI(() => rtbChat.AppendText("[Reconnect attempt failed] " + ex.Message + "\n"));
                return false;
            }
        }

        
        void ReceiveLoop()
        {
            try
            {
                while (!_closing)
                {
                    var ns = _ns;
                    if (ns == null) break;

                    string? json;
                    try
                    {
                        json = PacketIO.ReadJson(ns);
                    }
                    catch
                    {
                        break;
                    }

                    if (json == null) break;

                    PacketBase? basePkt;
                    try { basePkt = JsonSerializer.Deserialize<PacketBase>(json); }
                    catch { continue; }
                    if (basePkt == null) continue;

                    switch (basePkt.Type)
                    {
                        case PacketType.UserList:
                            HandleUserList(JsonSerializer.Deserialize<UserListPacket>(json)!);
                            break;

                        case PacketType.PublicKey:
                            HandlePubKey(JsonSerializer.Deserialize<PublicKeyPacket>(json)!);
                            break;

                        case PacketType.Chat:
                            HandleChat(JsonSerializer.Deserialize<ChatPacket>(json)!);
                            break;

                        case PacketType.ChatAck:
                            HandleChatAck(JsonSerializer.Deserialize<ChatAckPacket>(json)!);
                            break;

                        case PacketType.DeliveryReceipt:
                            HandleDeliveryReceipt(JsonSerializer.Deserialize<DeliveryReceiptPacket>(json)!);
                            break;

                        case PacketType.Typing:
                            HandleTyping(JsonSerializer.Deserialize<TypingPacket>(json)!);
                            break;

                        case PacketType.Recall:
                            HandleRecall(JsonSerializer.Deserialize<RecallPacket>(json)!);
                            break;
                    }
                }
            }
            finally
            {
                if (!_closing)
                {
                    InternalDisconnect("socket closed", sendLogout: false);
                    UI(() => rtbChat.AppendText("[Disconnected: socket closed]\n"));

                    if (!_manualDisconnect)
                        StartReconnectLoop("socket closed");
                }
            }
        }

        
        void HandleUserList(UserListPacket pkt)
        {
            if (pkt?.Users == null) return;

            lock (_convLock)
            {
                _userNames.Clear();
                _userOnline.Clear();

                foreach (var u in pkt.Users)
                {
                    if (string.IsNullOrEmpty(u.ClientId)) continue;
                    _userNames[u.ClientId] = u.User ?? "";
                    _userOnline[u.ClientId] = u.Online;
                }
            }

            RefreshUserListUi();
        }

        void RefreshUserListUi()
        {
            UI(() =>
            {
                lstUsers.BeginUpdate();
                try
                {
                    lstUsers.Items.Clear();

                    List<(string id, string name)> users;
                    lock (_convLock)
                    {
                        users = _userNames
                            .Select(kv => (id: kv.Key, name: kv.Value))
                            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }

                    if (users.Count == 0)
                    {
                        lstUsers.Items.Add("(no users)");
                        return;
                    }

                    foreach (var (id, name) in users)
                    {
                        
                        if (id == _clientId)
                        {
                            lstUsers.Items.Add($"{name} (you)");
                            continue;
                        }

                        bool online = false;
                        int unread = 0;
                        (long ts, string preview) last = default;

                        lock (_convLock)
                        {
                            online = _userOnline.TryGetValue(id, out var on) && on;
                            unread = _unread.TryGetValue(id, out var c) ? c : 0;
                            last = _lastMsg.TryGetValue(id, out var lm) ? lm : (0, "");
                        }

                        string badge = unread > 0 ? $" ({unread})" : "";
                        string status = online ? "" : " (offline)";

                        
                        string previewPart = "";
                        if (last.ts > 0 && !string.IsNullOrWhiteSpace(last.preview))
                        {
                            var t = DateTimeOffset.FromUnixTimeMilliseconds(last.ts).ToLocalTime();
                            previewPart = $" - {TrimPreview(last.preview, 22)} ({t:HH:mm})";
                        }

                        lstUsers.Items.Add($"{name}{status}{badge}{previewPart}");
                    }
                }
                finally
                {
                    lstUsers.EndUpdate();
                }
            });
        }

        static string TrimPreview(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        
        void HandlePubKey(PublicKeyPacket pkt)
        {
            if (string.IsNullOrEmpty(pkt.ClientId) || string.IsNullOrEmpty(pkt.PublicKey))
                return;

            _pubCache[pkt.ClientId] = pkt.PublicKey;

            lock (_pubLock)
            {
                if (_pubWaiters.TryGetValue(pkt.ClientId, out var tcs))
                {
                    tcs.TrySetResult(pkt.PublicKey);
                    _pubWaiters.Remove(pkt.ClientId);
                }
            }
        }

        async Task<string?> EnsurePubKeyAsync(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return null;

            if (_pubCache.TryGetValue(clientId, out var cached))
                return cached;

            TaskCompletionSource<string> tcs;

            lock (_pubLock)
            {
                if (_pubCache.TryGetValue(clientId, out cached))
                    return cached;

                if (!_pubWaiters.TryGetValue(clientId, out tcs!))
                {
                    tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pubWaiters[clientId] = tcs;

                    if (IsReallyConnected())
                        SendPacket(new GetPublicKeyPacket { ClientId = clientId });
                }
            }

            try
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                if (done != tcs.Task) return null;
                return await tcs.Task;
            }
            catch
            {
                return null;
            }
        }

        
        async void HandleChat(ChatPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.FromId) || string.IsNullOrEmpty(pkt.MessageId)) return;

            var k = $"{pkt.FromId}:{pkt.MessageId}";
            lock (_seenLock)
            {
                if (_seenIncoming.Contains(k))
                {
                    SendReceipt(pkt);
                    return;
                }
                _seenIncoming.Add(k);
            }

            string plain;
            try
            {
                var aesB64 = CryptoHelper.RsaDecryptBase64(pkt.EncKey, _privXml);
                var aes = Convert.FromBase64String(aesB64);
                plain = CryptoHelper.AesDecryptFromBase64(pkt.EncMsg, aes);
            }
            catch
            {
                UI(() => rtbChat.AppendText("[Decrypt failed]\n"));
                return;
            }

            bool verified = false;
            bool hasPub = false;
            var pub = await EnsurePubKeyAsync(pkt.FromId);
            if (!string.IsNullOrEmpty(pub))
            {
                hasPub = true;
                try { verified = CryptoHelper.VerifySignature(plain, pkt.Sig, pub); }
                catch { verified = false; }
            }

            SendReceipt(pkt);

            
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveHistory(new ChatLogEntry
            {
                Ts = now,
                Dir = "in",
                PeerId = pkt.FromId,
                PeerUser = pkt.FromUser,
                Text = plain,
                MessageId = pkt.MessageId ?? ""
            });

           
            lock (_convLock)
            {
                _lastMsg[pkt.FromId] = (now, plain);

                if (_activePeerId != pkt.FromId)
                {
                    _unread[pkt.FromId] = (_unread.TryGetValue(pkt.FromId, out var c) ? c : 0) + 1;
                }
            }

            var suffix = "";
            if (!hasPub) suffix = " [UNVERIFIED]";
            else if (!verified) suffix = " [BAD SIGNATURE]";

            if (_activePeerId == pkt.FromId)
                UI(() => rtbChat.AppendText($"{pkt.FromUser}: {plain}{suffix}\n"));
            else
                UI(() => rtbChat.AppendText($"[New message from {pkt.FromUser}] (click user to view)\n"));

            RefreshUserListUi();
        }

        void SendReceipt(ChatPacket pkt)
        {
            try
            {
                if (!IsReallyConnected()) return;

                SendPacket(new DeliveryReceiptPacket
                {
                    MessageId = pkt.MessageId ?? "",
                    FromId = _clientId,
                    ToId = pkt.FromId,
                    Status = "received",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            catch { }
        }

        
        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (!IsReallyConnected())
            {
                MessageBox.Show("Not connected");
                return;
            }

            if (_activePeerId == null || _activePeerUser == null)
            {
                MessageBox.Show("Select a user to chat.");
                return;
            }

            var toId = _activePeerId;

            if (toId == _clientId)
            {
                MessageBox.Show("Cannot send to yourself.");
                return;
            }

            var plain = txtMessage.Text.Trim();
            if (plain.Length == 0) return;

            var pub = await EnsurePubKeyAsync(toId);
            if (pub == null)
            {
                MessageBox.Show("Cannot get public key");
                return;
            }

            var msgId = Guid.NewGuid().ToString();
            var aes = CryptoHelper.GenerateAesKey();
            var encMsg = CryptoHelper.AesEncryptToBase64(plain, aes);
            var encKey = CryptoHelper.RsaEncryptBase64(Convert.ToBase64String(aes), pub);
            var sig = CryptoHelper.SignData(plain, _privXml);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var pkt = new ChatPacket
            {
                MessageId = msgId,
                Timestamp = now,
                FromId = _clientId,
                FromUser = _username!,
                ToId = toId,
                ToUser = _activePeerUser,
                EncKey = encKey,
                EncMsg = encMsg,
                Sig = sig
            };

            lock (_pendingLock)
            {
                _pending[msgId] = new PendingMsg
                {
                    Packet = pkt,
                    Attempts = 1,
                    FirstSentMs = now,
                    LastSentMs = now,
                    Stage = "new"
                };
            }

            SetStatus(msgId, "new", persistToHistory: false);

            SendPacket(pkt);

            SaveHistory(new ChatLogEntry
            {
                Ts = now,
                Dir = "out",
                PeerId = toId,
                PeerUser = _activePeerUser,
                Text = plain,
                MessageId = msgId
            });

            lock (_convLock)
            {
                _lastMsg[toId] = (now, plain);
            }

            _sentMessagePreview[msgId] = $"Me → {_activePeerUser}: {plain}";

          
            UI(() =>
            {
                var line = $"Me → {_activePeerUser}: {plain} {StatusToken("new")}\n";
                int start = rtbChat.TextLength;
                rtbChat.AppendText(line);

                int tokenPos = start + line.Length - 1 - 5;
                lock (_statusTokenLock)
                {
                    _statusTokenPos[msgId] = tokenPos;
                }

                rtbChat.ScrollToCaret();
            });

            RefreshUserListUi();
            txtMessage.Clear();

            TrySendTypingStop();
        }

        
        void HandleChatAck(ChatAckPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.MessageId)) return;

            lock (_pendingLock)
            {
                if (_pending.TryGetValue(pkt.MessageId, out var p))
                {
                    p.Stage = pkt.Status ?? p.Stage;
                    if (pkt.Status == "delivered_to_client")
                        _pending.Remove(pkt.MessageId);
                }
            }


            SetStatus(pkt.MessageId, pkt.Status ?? "", persistToHistory: true);

     
            UpdateStatusTokenInChatBox(pkt.MessageId, pkt.Status ?? "");
        }

        void HandleDeliveryReceipt(DeliveryReceiptPacket pkt)
        {
        
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.MessageId)) return;

            if (!string.IsNullOrEmpty(pkt.Status))
            {
                SetStatus(pkt.MessageId, pkt.Status, persistToHistory: true);
                UpdateStatusTokenInChatBox(pkt.MessageId, pkt.Status);
            }
        }

        string GetStatus(string msgId)
        {
            lock (_statusLock)
                return _msgStatus.TryGetValue(msgId, out var s) ? s : "";
        }

        void SetStatus(string msgId, string status, bool persistToHistory)
        {
            if (string.IsNullOrEmpty(msgId)) return;
            if (string.IsNullOrEmpty(status)) return;

            bool changed = false;
            lock (_statusLock)
            {
                if (!_msgStatus.TryGetValue(msgId, out var old) || old != status)
                {
                    _msgStatus[msgId] = status;
                    changed = true;
                }
            }

            if (!changed) return;

            if (persistToHistory)
            {
                string peerId = "";
                string peerUser = "";

                lock (_pendingLock)
                {
                    if (_pending.TryGetValue(msgId, out var p))
                    {
                        peerId = p.Packet.ToId;
                        peerUser = p.Packet.ToUser;
                    }
                }

                if (string.IsNullOrEmpty(peerId))
                {
                    peerId = _activePeerId ?? "";
                    peerUser = _activePeerUser ?? "";
                }

                SaveHistory(new ChatLogEntry
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Dir = "status",
                    PeerId = peerId,
                    PeerUser = peerUser,
                    Text = "",
                    MessageId = msgId,
                    Status = status
                });
            }
        }

        static string StatusToken(string? status)
        {
            return status switch
            {
                "accepted" => "[✓  ]",
                "delivered" => "[✓✓ ]",
                "offline_saved" => "[⚠  ]",
                "delivered_to_client" => "[✓✓✓]",
                "timeout" => "[⏱  ]",
                "new" => "[...]",
                _ => "[...]"
            };
        }

        void UpdateStatusTokenInChatBox(string msgId, string status)
        {
            int pos;
            lock (_statusTokenLock)
            {
                if (!_statusTokenPos.TryGetValue(msgId, out pos))
                    return;
            }

            UI(() =>
            {
                try
                {
                    rtbChat.Select(pos, 5);
                    rtbChat.SelectedText = StatusToken(status);
                    rtbChat.Select(rtbChat.TextLength, 0);
                    rtbChat.ScrollToCaret();
                }
                catch { }
            });
        }

        void ResendTick()
        {
            if (_closing) return;
            if (!IsReallyConnected()) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<ChatPacket> toResend = new();

            lock (_pendingLock)
            {
                foreach (var kv in _pending)
                {
                    var p = kv.Value;

                    if (p.Stage == "delivered_to_client" || p.Stage == "timeout")
                        continue;

                    int interval = (p.Stage == "offline_saved") ? 12_000 : 3_000;

                    if (now - p.FirstSentMs > 120_000)
                    {
                        p.Stage = "timeout";
                        SetStatus(p.Packet.MessageId ?? "", "timeout", persistToHistory: true);
                        UpdateStatusTokenInChatBox(p.Packet.MessageId ?? "", "timeout");
                        continue;
                    }

                    if (now - p.LastSentMs >= interval && p.Attempts < 6)
                    {
                        p.Attempts++;
                        p.LastSentMs = now;
                        toResend.Add(p.Packet);
                    }
                }
            }

            foreach (var pkt in toResend)
            {
                try { SendPacket(pkt); }
                catch { }
            }
        }

        void HandleRecall(RecallPacket pkt)
        {
            UI(() => rtbChat.AppendText($"[Message recalled] id={pkt.MessageId}\n"));
        }

        void TxtMessage_TextChanged(object? sender, EventArgs e)
        {
            _lastTypedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!IsReallyConnected()) return;
            if (_activePeerId == null) return;
            if (_activePeerId == _clientId) return;

            _typingSendDebounce.Stop();
            _typingSendDebounce.Start();

            _ = Task.Run(async () =>
            {
                var mark = _lastTypedAtMs;
                await Task.Delay(1200);
                if (_closing) return;
                if (!IsReallyConnected()) return;
                if (_activePeerId == null) return;

                if (_lastTypedAtMs == mark)
                    TrySendTypingStop();
            });
        }

        void TrySendTypingStart()
        {
            if (!IsReallyConnected()) return;
            if (_activePeerId == null) return;
            if (_activePeerId == _clientId) return;
            if (_typingSent) return;

            _typingSent = true;
            try
            {
                SendPacket(new TypingPacket
                {
                    FromId = _clientId,
                    FromUser = _username!,
                    ToId = _activePeerId,
                    IsTyping = true
                });
            }
            catch { }
        }

        void TrySendTypingStop()
        {
            if (!IsReallyConnected()) { _typingSent = false; return; }
            if (_activePeerId == null) { _typingSent = false; return; }
            if (!_typingSent) return;

            _typingSent = false;
            try
            {
                SendPacket(new TypingPacket
                {
                    FromId = _clientId,
                    FromUser = _username!,
                    ToId = _activePeerId,
                    IsTyping = false
                });
            }
            catch { }
        }

        void HandleTyping(TypingPacket pkt)
        {
            UI(() =>
            {
                if (_activePeerId == null) return;
                if (pkt.FromId != _activePeerId) return;

                ClearTyping();
                if (pkt.IsTyping)
                {
                    _typingUser = pkt.FromUser;
                    rtbChat.AppendText($"[{pkt.FromUser} is typing...]\n");
                    _typingUiClearTimer.Stop();
                    _typingUiClearTimer.Start();
                }
                else
                {
                    ClearTyping();
                }
            });
        }

        void ClearTyping()
        {
            if (_typingUser == null) return;
            rtbChat.Text = rtbChat.Text.Replace($"[{_typingUser} is typing...]\n", "");
            _typingUser = null;
        }

        void SendPacket<T>(T pkt)
        {
            var ns = _ns;
            if (ns == null) return;
            PacketIO.SendPacket(ns, pkt);
        }

        static string ExtractNameFromDisplay(string display)
        {
            var s = display;

            var dash = s.IndexOf(" - ");
            if (dash >= 0) s = s.Substring(0, dash);

            s = s.Replace(" (offline)", "")
                 .Replace(" (you)", "")
                 .Trim();

            int lastOpen = s.LastIndexOf(" (");
            int lastClose = s.LastIndexOf(")");
            if (lastOpen >= 0 && lastClose == s.Length - 1 && lastOpen < lastClose)
            {
                var inside = s.Substring(lastOpen + 2, lastClose - (lastOpen + 2));
                if (int.TryParse(inside, out _))
                    s = s.Substring(0, lastOpen).Trim();
            }

            return s;
        }

        string? GetClientIdByDisplay(string display)
        {
            var name = ExtractNameFromDisplay(display);

            lock (_convLock)
            {
                foreach (var kv in _userNames)
                    if (kv.Value == name)
                        return kv.Key;
            }
            return null;
        }

        private string LoadOrCreateClientId(string username)
        {
            Directory.CreateDirectory("keys");
            var safe = string.Join("_", username.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine("keys", $"clientid_{safe}.txt");

            if (File.Exists(path))
                return File.ReadAllText(path).Trim();

            var id = Guid.NewGuid().ToString();
            File.WriteAllText(path, id);
            return id;
        }

        private void RemoteClientForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _closing = true;
            _manualDisconnect = true;
            StopReconnectLoop();
            InternalDisconnect("closing", sendLogout: true);
        }

        class ChatLogEntry
        {
            public long Ts { get; set; }
            public string Dir { get; set; } = "";   
            public string PeerId { get; set; } = "";
            public string PeerUser { get; set; } = "";
            public string Text { get; set; } = "";
            public string MessageId { get; set; } = "";
            public string Status { get; set; } = "";
        }

        string GetHistoryFile(string peerId)
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history", _clientId);
            Directory.CreateDirectory(root);
            return Path.Combine(root, $"{peerId}.jsonl");
        }

        void SaveHistory(ChatLogEntry e)
        {
            try
            {
                if (string.IsNullOrEmpty(e.PeerId)) return;
                var file = GetHistoryFile(e.PeerId);
                File.AppendAllText(file, JsonSerializer.Serialize(e) + Environment.NewLine);
            }
            catch { }
        }

        void LoadHistoryToChatBox(string peerId, string peerUser)
        {
            try
            {
                var file = GetHistoryFile(peerId);

                UI(() =>
                {
                    rtbChat.Clear();

                    lock (_statusTokenLock)
                    {
                        _statusTokenPos.Clear();
                    }
                });

                if (!File.Exists(file))
                {
                    UI(() => rtbChat.AppendText($"--- Chat with {peerUser} ---\n"));
                    return;
                }

                var entries = new List<ChatLogEntry>();
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<ChatLogEntry>(line);
                        if (e != null) entries.Add(e);
                    }
                    catch { }
                }

                var statusByMsg = new Dictionary<string, string>();
                foreach (var e in entries)
                {
                    if (e.Dir == "status" && !string.IsNullOrEmpty(e.MessageId) && !string.IsNullOrEmpty(e.Status))
                        statusByMsg[e.MessageId] = e.Status;
                }

                UI(() => rtbChat.AppendText($"--- Chat with {peerUser} ---\n"));

                foreach (var e in entries)
                {
                    if (e.Dir == "out")
                    {
                        var st = (!string.IsNullOrEmpty(e.MessageId) && statusByMsg.TryGetValue(e.MessageId, out var s))
                            ? s
                            : "new";

                        UI(() =>
                        {
                            var line = $"Me → {peerUser}: {e.Text} {StatusToken(st)}\n";
                            int start = rtbChat.TextLength;
                            rtbChat.AppendText(line);

                            if (!string.IsNullOrEmpty(e.MessageId))
                            {
                                int tokenPos = start + line.Length - 1 - 5;
                                lock (_statusTokenLock)
                                {
                                    _statusTokenPos[e.MessageId] = tokenPos;
                                }
                            }
                        });
                    }
                    else if (e.Dir == "in")
                    {
                        UI(() => rtbChat.AppendText($"{peerUser}: {e.Text}\n"));
                    }
                }

                UI(() =>
                {
                    rtbChat.Select(rtbChat.TextLength, 0);
                    rtbChat.ScrollToCaret();
                });
            }
            catch { }
        }

        void SwitchConversation(string peerId, string peerUser)
        {
            _activePeerId = peerId;
            _activePeerUser = peerUser;

            lock (_convLock)
            {
                _unread[peerId] = 0;
            }

            LoadHistoryToChatBox(peerId, peerUser);

            RefreshUserListUi();

            TrySendTypingStop();
        }

        void RebuildConversationSummariesFromDisk()
        {
            try
            {
                if (string.IsNullOrEmpty(_clientId)) return;

                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history", _clientId);
                Directory.CreateDirectory(root);

                lock (_convLock)
                {
                    _lastMsg.Clear();
                }

                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl"))
                {
                    var peerId = Path.GetFileNameWithoutExtension(file);
                    ChatLogEntry? last = null;

                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var e = JsonSerializer.Deserialize<ChatLogEntry>(line);
                            if (e != null && (e.Dir == "in" || e.Dir == "out"))
                                last = e;
                        }
                        catch { }
                    }

                    if (last != null)
                    {
                        lock (_convLock)
                        {
                            _lastMsg[peerId] = (last.Ts, last.Text);
                        }
                    }
                }
            }
            catch { }
        }


        private void UI(Action a)
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private bool IsReallyConnected()
        {
            if (!_connected) return false;
            if (_tcp == null || _ns == null) return false;

            try
            {
                var s = _tcp.Client;
                if (s == null) return false;

                bool readReady = s.Poll(0, SelectMode.SelectRead);
                bool noData = (s.Available == 0);
                if (readReady && noData) return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void SetUiConnected(bool connected)
        {
            UI(() =>
            {
                btnConnect.Text = connected ? "Disconnect" : "Connect";
                txtServerIP.Enabled = !connected;
                txtPort.Enabled = !connected;
                txtUser.Enabled = !connected;
            });
        }


        private void btnGetPubKey_Click(object sender, EventArgs e) { }
        private void rtbChat_TextChanged(object sender, EventArgs e) { }
        private void txtServerIP_TextChanged(object sender, EventArgs e) { }
        private void label2_Click(object sender, EventArgs e) { }
        private void txtPort_TextChanged(object sender, EventArgs e) { }
        private void txtUser_TextChanged(object sender, EventArgs e) { }
    }
}
