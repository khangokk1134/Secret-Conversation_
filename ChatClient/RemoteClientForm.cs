using System;
using System.Collections.Generic;
using System.IO;
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

        // user pressed Disconnect => do not auto reconnect
        volatile bool _manualDisconnect = false;

        // reconnect loop control
        CancellationTokenSource? _reconnectCts;
        int _reconnectRunning = 0;

        // remember last endpoint for reconnect
        string _serverIp = "";
        int _serverPort = 5000;

        string _clientId = "";
        string? _username;

        string _pubXml = "", _privXml = "";

        readonly Dictionary<string, string> _pubCache = new();
        readonly Dictionary<string, TaskCompletionSource<string>> _pubWaiters = new();
        readonly object _pubLock = new();

        // user list
        readonly Dictionary<string, string> _userNames = new();
        string? _activePeerId = null;

        // message preview for ack display
        readonly Dictionary<string, string> _sentMessagePreview = new();

        // idempotent incoming
        readonly HashSet<string> _seenIncoming = new();
        readonly object _seenLock = new();

        // resend
        class PendingMsg
        {
            public ChatPacket Packet = new ChatPacket();
            public int Attempts = 0;
            public long FirstSentMs;
            public long LastSentMs;
            public string Stage = "new"; // new/accepted/offline_saved/delivered/delivered_to_client/timeout
        }

        readonly Dictionary<string, PendingMsg> _pending = new();
        readonly object _pendingLock = new();

        System.Windows.Forms.Timer _resendTimer;
        System.Windows.Forms.Timer _typingTimer;
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

            _typingTimer = new System.Windows.Forms.Timer();
            _typingTimer.Interval = 1500;
            _typingTimer.Tick += (s, e) =>
            {
                ClearTyping();
                _typingTimer.Stop();
            };
            txtMessage.TextChanged += TxtMessage_TextChanged;

            lstUsers.SelectedIndexChanged += (s, e) =>
            {
                if (lstUsers.SelectedItem == null) return;

                var display = lstUsers.SelectedItem.ToString()!;
                var peerId = GetClientIdByName(display);
                if (peerId == null) return;

                // chặn chọn chính mình
                if (peerId == _clientId)
                {
                    UI(() => lstUsers.ClearSelected());
                    return;
                }

                var peerUser = display
                    .Replace(" (offline)", "")
                    .Replace(" (you)", "")
                    .Trim();

                SwitchConversation(peerId, peerUser);
            };

            _resendTimer = new System.Windows.Forms.Timer();
            _resendTimer.Interval = 1000;
            _resendTimer.Tick += (s, e) => ResendTick();
            _resendTimer.Stop();

            SetUiConnected(false);
            this.FormClosing += RemoteClientForm_FormClosing;
        }

        // ================= CONNECT (TOGGLE) =================
        private void btnConnect_Click(object sender, EventArgs e)
        {
            // Toggle: đang connected -> bấm là disconnect
            if (IsReallyConnected())
            {
                Disconnect("user");
                return;
            }

            // User explicitly clicks Connect => allow auto reconnect after this point
            _manualDisconnect = false;

            // stop any previous reconnect loop
            StopReconnectLoop();

            // reset trạng thái cũ
            _connected = false;
            InternalDisconnect("reset before connect", sendLogout: false);

            // clear UI list trước khi connect để đỡ hiểu nhầm
            UI(() =>
            {
                lstUsers.Items.Clear();
                _userNames.Clear();
            });

            _username = txtUser.Text.Trim();
            if (string.IsNullOrEmpty(_username)) return;

            // remember endpoint for reconnect
            _serverIp = txtServerIP.Text.Trim();
            if (!int.TryParse(txtPort.Text, out _serverPort))
                return;

            _clientId = LoadOrCreateClientId(_username);

            _closing = false;

            // Try connect once; on failure, start auto reconnect loop
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

            // user initiated disconnect => do not auto reconnect
            _manualDisconnect = true;
            StopReconnectLoop();

            InternalDisconnect(reason, sendLogout: true);

            // clear users khi disconnect để nhìn rõ trạng thái
            UI(() =>
            {
                lstUsers.Items.Clear();
                _userNames.Clear();
            });

            UI(() => rtbChat.AppendText($"[Disconnected: {reason}]\n"));
        }

        private void InternalDisconnect(string reason, bool sendLogout)
        {
            _connected = false;

            try { _resendTimer.Stop(); } catch { }
            try { _typingTimer.Stop(); } catch { }

            lock (_pubLock)
            {
                foreach (var kv in _pubWaiters)
                    kv.Value.TrySetCanceled();
                _pubWaiters.Clear();
            }

            try { ClearTyping(); } catch { }

            // best-effort logout
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

        // Backward-compatible wrapper for existing calls
        private void InternalDisconnect(string reason)
            => InternalDisconnect(reason, sendLogout: true);

        // ================= RECONNECT =================
        private void StartReconnectLoop(string reason)
        {
            if (_closing) return;
            if (_manualDisconnect) return;

            if (Interlocked.Exchange(ref _reconnectRunning, 1) == 1)
                return; // already running

            StopReconnectLoop();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;

            UI(() => rtbChat.AppendText($"[Auto-reconnect started: {reason}]\n"));

            _ = Task.Run(async () =>
            {
                // backoff sequence (ms)
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

                        // cleanup any dead sockets before retry
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

                // Connect with cancellation via Task.WhenAny
                var connectTask = tcp.ConnectAsync(_serverIp, _serverPort);
                var done = await Task.WhenAny(connectTask, Task.Delay(8000, token));
                if (done != connectTask)
                {
                    try { tcp.Close(); } catch { }
                    return false;
                }

                // propagate connect exceptions
                await connectTask;

                var ns = tcp.GetStream();

                _tcp = tcp;
                _ns = ns;

                _connected = true;
                SetUiConnected(true);

                // Register
                SendPacket(new RegisterPacket
                {
                    ClientId = _clientId,
                    User = _username!,
                    PublicKey = _pubXml
                });

                // start receive loop
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

        // ================= RECEIVE LOOP =================
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
                        break; // socket closed
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

                    // auto reconnect unless user manually disconnected
                    if (!_manualDisconnect)
                        StartReconnectLoop("socket closed");
                }
            }
        }

        // ================= USER LIST =================
        void HandleUserList(UserListPacket pkt)
        {
            if (pkt?.Users == null) return;

            UI(() =>
            {
                lstUsers.BeginUpdate();
                try
                {
                    lstUsers.Items.Clear();
                    _userNames.Clear();

                    foreach (var u in pkt.Users)
                    {
                        if (string.IsNullOrEmpty(u.ClientId)) continue;
                        _userNames[u.ClientId] = u.User ?? "";

                        // ✅ HIỂN THỊ CHÍNH MÌNH để khỏi “trống list” khi chỉ có 1 client
                        if (u.ClientId == _clientId)
                        {
                            lstUsers.Items.Add($"{u.User} (you)");
                            continue;
                        }

                        lstUsers.Items.Add(u.Online ? u.User : $"{u.User} (offline)");
                    }

                    if (lstUsers.Items.Count == 0)
                        lstUsers.Items.Add("(no users)");
                }
                finally
                {
                    lstUsers.EndUpdate();
                }
            });
        }

        // ================= PUBLIC KEY =================
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

        // ================= CHAT RECEIVE =================
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

            SaveHistory(new ChatLogEntry
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Dir = "in",
                PeerId = pkt.FromId,
                PeerUser = pkt.FromUser,
                Text = plain,
                MessageId = pkt.MessageId ?? ""
            });

            var suffix = "";
            if (!hasPub) suffix = " [UNVERIFIED]";
            else if (!verified) suffix = " [BAD SIGNATURE]";

            if (_activePeerId == pkt.FromId)
                UI(() => rtbChat.AppendText($"{pkt.FromUser}: {plain}{suffix}\n"));
            else
                ShowNewMessageHint(pkt.FromUser);
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

        // ================= SEND CHAT =================
        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (!IsReallyConnected())
            {
                MessageBox.Show("Not connected");
                return;
            }

            if (lstUsers.SelectedItem == null) return;

            var display = lstUsers.SelectedItem.ToString()!;
            var toId = GetClientIdByName(display);
            if (toId == null) return;

            // chặn gửi cho chính mình
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

            var pkt = new ChatPacket
            {
                MessageId = msgId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FromId = _clientId,
                FromUser = _username!,
                ToId = toId,
                ToUser = display.Replace(" (offline)", "").Replace(" (you)", "").Trim(),
                EncKey = encKey,
                EncMsg = encMsg,
                Sig = sig
            };

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

            SendPacket(pkt);

            SaveHistory(new ChatLogEntry
            {
                Ts = now,
                Dir = "out",
                PeerId = toId,
                PeerUser = display.Replace(" (offline)", "").Replace(" (you)", "").Trim(),
                Text = plain,
                MessageId = msgId
            });

            _sentMessagePreview[msgId] = $"Me → {display}: {plain}";
            UI(() => rtbChat.AppendText($"Me → {display}: {plain}\n"));
            txtMessage.Clear();
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

            UI(() =>
            {
                var text = _sentMessagePreview.TryGetValue(pkt.MessageId, out var preview)
                    ? preview
                    : "(unknown message)";

                if (pkt.Status == "accepted")
                    rtbChat.AppendText($"[✓ Server accepted] {text}\n");
                else if (pkt.Status == "offline_saved")
                    rtbChat.AppendText($"[⚠ Offline – saved] {text}\n");
                else if (pkt.Status == "delivered")
                    rtbChat.AppendText($"[✓ Delivered to receiver socket] {text}\n");
                else if (pkt.Status == "delivered_to_client")
                    rtbChat.AppendText($"[✓ Delivered to client (2-way ACK)] {text}\n");
                else
                    rtbChat.AppendText($"[ACK {pkt.Status}] {text}\n");
            });
        }

        void HandleDeliveryReceipt(DeliveryReceiptPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.MessageId)) return;

            if (pkt.Status == "delivered_to_client")
            {
                lock (_pendingLock) { _pending.Remove(pkt.MessageId); }
            }

            UI(() =>
            {
                var text = _sentMessagePreview.TryGetValue(pkt.MessageId, out var preview)
                    ? preview
                    : "(unknown message)";
                rtbChat.AppendText($"[Receipt {pkt.Status}] {text}\n");
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

                    if (now - p.FirstSentMs > 120_000)
                    {
                        p.Stage = "timeout";
                        continue;
                    }

                    if (now - p.LastSentMs >= 3000 && p.Attempts < 6)
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
            if (!IsReallyConnected()) return;
            if (lstUsers.SelectedItem == null) return;

            var toId = GetClientIdByName(lstUsers.SelectedItem.ToString()!);
            if (toId == null) return;
            if (toId == _clientId) return;

            SendPacket(new TypingPacket
            {
                FromId = _clientId,
                FromUser = _username!,
                ToId = toId,
                IsTyping = true
            });

            _typingTimer.Stop();
            _typingTimer.Start();
        }

        void HandleTyping(TypingPacket pkt)
        {
            UI(() =>
            {
                ClearTyping();
                if (pkt.IsTyping)
                {
                    _typingUser = pkt.FromUser;
                    rtbChat.AppendText($"[{pkt.FromUser} is typing...]\n");
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

        string? GetClientIdByName(string display)
        {
            var name = display
                .Replace(" (offline)", "")
                .Replace(" (you)", "")
                .Trim();

            foreach (var kv in _userNames)
                if (kv.Value == name)
                    return kv.Key;
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
                var file = GetHistoryFile(e.PeerId);
                File.AppendAllText(file, JsonSerializer.Serialize(e) + Environment.NewLine);
            }
            catch { }
        }

        void LoadHistoryToChatBox(string peerId)
        {
            try
            {
                var file = GetHistoryFile(peerId);

                UI(() => rtbChat.Clear());

                if (!File.Exists(file)) return;

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ChatLogEntry? e = null;
                    try { e = JsonSerializer.Deserialize<ChatLogEntry>(line); }
                    catch { continue; }
                    if (e == null) continue;

                    if (e.Dir == "out")
                        UI(() => rtbChat.AppendText($"Me → {e.PeerUser}: {e.Text}\n"));
                    else if (e.Dir == "in")
                        UI(() => rtbChat.AppendText($"{e.PeerUser}: {e.Text}\n"));
                    else if (e.Dir == "status")
                        UI(() => rtbChat.AppendText($"[{e.Status}] {e.Text}\n"));
                }
            }
            catch { }
        }

        void SwitchConversation(string peerId, string peerUser)
        {
            _activePeerId = peerId;
            LoadHistoryToChatBox(peerId);
            UI(() => rtbChat.AppendText($"--- Chat with {peerUser} ---\n"));
        }

        void ShowNewMessageHint(string fromUser)
        {
            UI(() => rtbChat.AppendText($"[New message from {fromUser}] (click user to view)\n"));
        }

        // Designer stubs
        private void btnGetPubKey_Click(object sender, EventArgs e) { }
        private void rtbChat_TextChanged(object sender, EventArgs e) { }
        private void txtServerIP_TextChanged(object sender, EventArgs e) { }
        private void label2_Click(object sender, EventArgs e) { }
        private void txtPort_TextChanged(object sender, EventArgs e) { }
        private void txtUser_TextChanged(object sender, EventArgs e) { }

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
    }
}
