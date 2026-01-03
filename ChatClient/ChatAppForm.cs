using System;
using System.Collections.Generic;
using System.Drawing;
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
    public partial class ChatAppForm : Form
    {
        // ================= NETWORK =================
        private TcpClient? _tcp;
        private NetworkStream? _ns;
        private Thread? _recvThread;

        private volatile bool _closing = false;
        private volatile bool _connected = false;

        private string _clientId = "";
        private string? _username;

        private string _pubXml = "", _privXml = "";

        private readonly Dictionary<string, string> _pubCache = new();
        private readonly Dictionary<string, TaskCompletionSource<string>> _pubWaiters = new();
        private readonly object _pubLock = new();

        // incoming idempotent
        private readonly HashSet<string> _seenIncoming = new();
        private readonly object _seenLock = new();

        // pending resend + stage
        private sealed class PendingMsg
        {
            public ChatPacket Packet = new ChatPacket();
            public int Attempts;
            public long FirstSentMs;
            public long LastSentMs;
            public string Stage = "new"; // new/accepted/offline_saved/delivered/delivered_to_client/timeout
            public string PlainPreview = "";
        }

        private readonly Dictionary<string, PendingMsg> _pending = new();
        private readonly object _pendingLock = new();

        private System.Windows.Forms.Timer _resendTimer = null!;
        private System.Windows.Forms.Timer _typingDebounce = null!;

        private string? _activePeerId;
        private string? _activePeerName;

        // ================= DATA MODEL (CONVOS) =================
        private sealed class ConvoState
        {
            public string PeerId = "";
            public string Name = "";
            public string Last = "";
            public int Unread = 0;
            public bool Online = false;
            public long LastTs = 0;
        }

        private readonly Dictionary<string, ConvoState> _convos = new();  // peerId -> state
        private readonly Dictionary<string, string> _userNames = new();   // clientId -> username

        // column resize guard (FIX stackoverflow)
        private bool _fixingColumns = false;

        // when we update list programmatically, prevent selection recursion
        private bool _updatingConvoList = false;

        public ChatAppForm()
        {
            InitializeComponent();

            Text = "Secure Chat Client";
            MinimumSize = new Size(980, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

            LoadKeys();
            HookUI();

            _resendTimer = new System.Windows.Forms.Timer();
            _resendTimer.Interval = 1000;
            _resendTimer.Tick += (_, __) => ResendTick();

            _typingDebounce = new System.Windows.Forms.Timer();
            _typingDebounce.Interval = 350;
            _typingDebounce.Tick += (_, __) =>
            {
                _typingDebounce.Stop();
                SendTyping(true);
            };

            FormClosing += (_, __) =>
            {
                _closing = true;
                InternalDisconnect("closing");
            };

            // IMPORTANT: hook resize ONCE (đừng hook trong AddBubble)
            chatFlow.Resize += (_, __) => ReflowBubbles();

            // ✅ FIX: luôn căn lại Online dưới Chat title (DPI/Resize/Text change)
            Shown += (_, __) => FixChatHeaderLayout();
            Resize += (_, __) => FixChatHeaderLayout();
            lblChatTitle.SizeChanged += (_, __) => FixChatHeaderLayout();
            lblChatSub.SizeChanged += (_, __) => FixChatHeaderLayout();

            SetUiConnected(false);
            SetStatus("Enter server/port/user then click Connect.", muted: true);

            FixConvoColumns();
            FixChatHeaderLayout();
        }

        private void ChatAppForm_Load(object sender, EventArgs e)
        {
            FixChatHeaderLayout();
        }

        // ================= FIX: CHAT HEADER LAYOUT (ONLINE NOT OVERLAP) =================
        private void FixChatHeaderLayout()
        {
            // Chỉ can thiệp vị trí label, không đụng Designer
            if (IsDisposed) return;
            if (lblChatTitle == null || lblChatSub == null) return;

            UI(() =>
            {
                // đảm bảo autosize để lấy đúng kích thước
                lblChatTitle.AutoSize = true;
                lblChatSub.AutoSize = true;

                // đặt Online xuống dưới Chat title, cách 2px
                lblChatSub.Left = lblChatTitle.Left;
                lblChatSub.Top = lblChatTitle.Bottom + 2;

                // nếu title quá dài, wrap vẫn ok vì autosize,
                // nhưng nếu muốn chắc hơn thì giới hạn width theo vùng chứa
                if (lblChatTitle.Parent != null)
                {
                    int maxW = Math.Max(200, lblChatTitle.Parent.ClientSize.Width - lblChatTitle.Left - 10);
                    lblChatTitle.MaximumSize = new Size(maxW, 0);
                    lblChatSub.MaximumSize = new Size(maxW, 0);
                }
            });
        }

        // ================= UI HOOKS =================
        private void HookUI()
        {
            btnConnect.Click += (_, __) => ToggleConnect();

            btnSend.Click += async (_, __) => await SendMessageAsync();

            txtMessage.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    await SendMessageAsync();
                }
            };

            txtMessage.TextChanged += (_, __) =>
            {
                if (!IsReallyConnected()) return;
                if (string.IsNullOrEmpty(_activePeerId)) return;
                if (_activePeerId == _clientId) return;

                _typingDebounce.Stop();
                _typingDebounce.Start();
            };

            lvConvos.SelectedIndexChanged += (_, __) =>
            {
                if (_updatingConvoList) return;
                if (lvConvos.SelectedItems.Count == 0) return;

                var it = lvConvos.SelectedItems[0];
                if (it.Tag is not ConvoState c) return;

                if (c.PeerId == _clientId) return;

                OpenConversation(c.PeerId, c.Name);
            };

            lvConvos.SizeChanged += (_, __) =>
            {
                if (!_fixingColumns)
                    BeginInvoke(new Action(FixConvoColumns));
            };

            lvConvos.ColumnWidthChanged += (_, __) =>
            {
                if (!_fixingColumns)
                    BeginInvoke(new Action(FixConvoColumns));
            };
        }

        // ================= FIX COLUMNS (NO STACK OVERFLOW) =================
        private void FixConvoColumns()
        {
            if (_fixingColumns) return;
            if (lvConvos.IsDisposed) return;

            _fixingColumns = true;
            try
            {
                int w = lvConvos.ClientSize.Width;
                if (w <= 10) return;

                int unreadW = 95;  // tăng lên để đủ chữ "Unread"
                int lastW = 100;   // tăng lên để đủ chữ "Last" + nội dung
                int nameW = Math.Max(120, w - unreadW - lastW - 8);

                colUnread.Width = unreadW;
                colLast.Width = lastW;
                colName.Width = nameW;
            }
            finally
            {
                _fixingColumns = false;
            }
        }

        // ================= STATUS/UI =================
        private void SetStatus(string text, bool muted = false)
        {
            if (IsDisposed) return;
            UI(() =>
            {
                lblStatus.Text = text;
                lblStatus.ForeColor = muted ? Color.Gray : Color.FromArgb(20, 90, 20);
            });
        }

        private void SetUiConnected(bool connected)
        {
            UI(() =>
            {
                btnConnect.Text = connected ? "Disconnect" : "Connect";
                txtServer.Enabled = !connected;
                txtPort.Enabled = !connected;
                txtUser.Enabled = !connected;

                btnSend.Enabled = connected;
                txtMessage.Enabled = connected;
            });
        }

        // ================= KEYS + CLIENTID =================
        private void LoadKeys()
        {
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

        // ================= CONNECT / DISCONNECT =================
        private void ToggleConnect()
        {
            if (IsReallyConnected())
            {
                Disconnect("user");
                return;
            }

            _connected = false;
            InternalDisconnect("reset");

            _username = txtUser.Text.Trim();
            if (string.IsNullOrEmpty(_username))
            {
                SetStatus("User is required.", muted: true);
                return;
            }

            _clientId = LoadOrCreateClientId(_username);

            try
            {
                _closing = false;

                _tcp = new TcpClient();
                _tcp.Connect(txtServer.Text.Trim(), int.Parse(txtPort.Text.Trim()));
                _ns = _tcp.GetStream();

                _connected = true;
                SetUiConnected(true);
                SetStatus("Connected.", muted: false);

                // clear UI
                UI(() =>
                {
                    chatFlow.Controls.Clear();
                    lvConvos.Items.Clear();
                    lblChatTitle.Text = "Chat";
                    lblChatSub.Text = "";
                });
                _convos.Clear();
                _userNames.Clear();

                FixChatHeaderLayout();

                SendPacket(new RegisterPacket
                {
                    ClientId = _clientId,
                    User = _username!,
                    PublicKey = _pubXml
                });

                _recvThread = new Thread(ReceiveLoop) { IsBackground = true };
                _recvThread.Start();

                _resendTimer.Start();
            }
            catch (Exception ex)
            {
                InternalDisconnect("connect failed");
                SetStatus("ERROR: " + ex.Message, muted: true);
            }
        }

        private void Disconnect(string reason)
        {
            InternalDisconnect(reason);

            UI(() =>
            {
                chatFlow.Controls.Clear();
                lvConvos.Items.Clear();
            });

            _convos.Clear();
            _userNames.Clear();

            lblChatTitle.Text = "Chat";
            lblChatSub.Text = "";
            _activePeerId = null;
            _activePeerName = null;

            FixChatHeaderLayout();

            SetStatus($"Disconnected: {reason}", muted: true);
        }

        private void InternalDisconnect(string reason)
        {
            _connected = false;

            try { _resendTimer.Stop(); } catch { }
            try { _typingDebounce.Stop(); } catch { }

            lock (_pubLock)
            {
                foreach (var kv in _pubWaiters)
                    kv.Value.TrySetCanceled();
                _pubWaiters.Clear();
            }

            // best-effort logout
            try
            {
                if (_ns != null && !string.IsNullOrEmpty(_clientId))
                    SendPacket(new LogoutPacket { ClientId = _clientId });
            }
            catch { }

            try { _ns?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }

            _ns = null;
            _tcp = null;

            SetUiConnected(false);
        }

        // ================= RECEIVE LOOP =================
        private void ReceiveLoop()
        {
            try
            {
                while (!_closing)
                {
                    var ns = _ns;
                    if (ns == null) break;

                    string? json;
                    try { json = PacketIO.ReadJson(ns); }
                    catch { break; }

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
                            // optional
                            break;
                        case PacketType.Recall:
                            // optional
                            break;
                    }
                }
            }
            finally
            {
                if (!_closing)
                {
                    InternalDisconnect("socket closed");
                    SetStatus("Disconnected: socket closed", muted: true);
                }
            }
        }

        // ================= USERLIST -> CONVO LIST =================
        private void HandleUserList(UserListPacket pkt)
        {
            if (pkt?.Users == null) return;

            foreach (var u in pkt.Users)
            {
                if (string.IsNullOrEmpty(u.ClientId)) continue;

                _userNames[u.ClientId] = u.User ?? "";

                if (u.ClientId == _clientId) continue;

                if (!_convos.TryGetValue(u.ClientId, out var c))
                {
                    c = new ConvoState
                    {
                        PeerId = u.ClientId,
                        Name = u.User ?? u.ClientId,
                        Online = u.Online
                    };
                    _convos[u.ClientId] = c;
                }
                else
                {
                    c.Name = u.User ?? c.Name;
                    c.Online = u.Online;
                }
            }

            RefreshConvoList();
            UI(() =>
            {
                if (!string.IsNullOrEmpty(_activePeerId) && _convos.TryGetValue(_activePeerId, out var cur))
                    lblChatSub.Text = cur.Online ? "Online" : "Offline";
            });

            FixChatHeaderLayout();
        }

        private void RefreshConvoList()
        {
            if (IsDisposed) return;

            UI(() =>
            {
                _updatingConvoList = true;
                try
                {
                    var selected = (lvConvos.SelectedItems.Count > 0 && lvConvos.SelectedItems[0].Tag is ConvoState sel)
                        ? sel.PeerId
                        : _activePeerId;

                    lvConvos.BeginUpdate();
                    lvConvos.Items.Clear();

                    foreach (var c in _convos.Values
                        .OrderByDescending(x => x.LastTs)
                        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var name = c.Name + (c.Online ? "" : " (offline)");

                        var it = new ListViewItem(name);
                        it.SubItems.Add(string.IsNullOrEmpty(c.Last) ? "" : c.Last);
                        it.SubItems.Add(c.Unread > 0 ? c.Unread.ToString() : "");
                        it.Tag = c;

                        if (c.Unread > 0)
                            it.Font = new Font(Font, FontStyle.Bold);

                        lvConvos.Items.Add(it);

                        if (!string.IsNullOrEmpty(selected) && c.PeerId == selected)
                            it.Selected = true;
                    }

                    FixConvoColumns();
                }
                finally
                {
                    lvConvos.EndUpdate();
                    _updatingConvoList = false;
                }
            });
        }

        // ================= PUBKEY CACHE =================
        private void HandlePubKey(PublicKeyPacket pkt)
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

        private async Task<string?> EnsurePubKeyAsync(string clientId)
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
                        SendPacket(new GetPublicKeyPacket { ClientId = clientId, FromId = _clientId });
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
        private async void HandleChat(ChatPacket pkt)
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
                return;
            }

            var pub = await EnsurePubKeyAsync(pkt.FromId);
            bool verified = false;
            if (!string.IsNullOrEmpty(pub))
            {
                try { verified = CryptoHelper.VerifySignature(plain, pkt.Sig, pub); }
                catch { verified = false; }
            }

            SendReceipt(pkt);

            var fromName = pkt.FromUser;
            if (string.IsNullOrEmpty(fromName) && _userNames.TryGetValue(pkt.FromId, out var n))
                fromName = n;

            if (!_convos.TryGetValue(pkt.FromId, out var c))
            {
                c = new ConvoState { PeerId = pkt.FromId, Name = fromName ?? pkt.FromId };
                _convos[pkt.FromId] = c;
            }

            c.Last = TrimPreview(plain);
            c.LastTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_activePeerId != pkt.FromId)
                c.Unread++;

            RefreshConvoList();

            SaveHistory(new ChatLogEntry
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Dir = "in",
                PeerId = pkt.FromId,
                PeerUser = c.Name,
                Text = plain,
                MessageId = pkt.MessageId ?? "",
                Status = verified ? "" : "UNVERIFIED"
            });

            if (_activePeerId == pkt.FromId)
                AddBubble(isMine: false, who: c.Name, text: plain, ts: DateTime.Now, status: verified ? "" : "UNVERIFIED");
        }

        private void SendReceipt(ChatPacket pkt)
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

        // ================= ACK / RECEIPT =================
        private void HandleChatAck(ChatAckPacket pkt)
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

            UI(() => UpdateLastMineBubbleStatus(pkt.MessageId, pkt.Status ?? ""));
        }

        private void HandleDeliveryReceipt(DeliveryReceiptPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.MessageId)) return;

            if (pkt.Status == "delivered_to_client")
            {
                lock (_pendingLock) { _pending.Remove(pkt.MessageId); }
            }
        }

        // ================= SEND MESSAGE =================
        private async Task SendMessageAsync()
        {
            if (!IsReallyConnected())
            {
                SetStatus("Not connected.", muted: true);
                return;
            }

            if (string.IsNullOrEmpty(_activePeerId) || _activePeerId == _clientId)
            {
                SetStatus("Select a conversation first.", muted: true);
                return;
            }

            var plain = txtMessage.Text.Trim();
            if (plain.Length == 0) return;

            var toId = _activePeerId!;
            var toName = _activePeerName ?? toId;

            var pub = await EnsurePubKeyAsync(toId);
            if (pub == null)
            {
                SetStatus("Cannot get receiver public key.", muted: true);
                return;
            }

            var msgId = Guid.NewGuid().ToString();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var aes = CryptoHelper.GenerateAesKey();
            var encMsg = CryptoHelper.AesEncryptToBase64(plain, aes);
            var encKey = CryptoHelper.RsaEncryptBase64(Convert.ToBase64String(aes), pub);
            var sig = CryptoHelper.SignData(plain, _privXml);

            var pkt = new ChatPacket
            {
                MessageId = msgId,
                Timestamp = nowMs,
                FromId = _clientId,
                FromUser = _username ?? "",
                ToId = toId,
                ToUser = toName,
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
                    FirstSentMs = nowMs,
                    LastSentMs = nowMs,
                    Stage = "new",
                    PlainPreview = plain
                };
            }

            SendPacket(pkt);

            if (_convos.TryGetValue(toId, out var c))
            {
                c.Last = TrimPreview(plain);
                c.LastTs = nowMs;
                RefreshConvoList();
            }

            SaveHistory(new ChatLogEntry
            {
                Ts = nowMs,
                Dir = "out",
                PeerId = toId,
                PeerUser = toName,
                Text = plain,
                MessageId = msgId,
                Status = "sent"
            });

            AddBubble(isMine: true, who: "Me", text: plain, ts: DateTime.Now, status: "…", messageId: msgId);

            txtMessage.Clear();
            txtMessage.Focus();
        }

        // ================= RESEND =================
        private void ResendTick()
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

        // ================= TYPING (OPTIONAL) =================
        private void SendTyping(bool isTyping)
        {
            try
            {
                if (!IsReallyConnected()) return;
                if (string.IsNullOrEmpty(_activePeerId)) return;

                SendPacket(new TypingPacket
                {
                    FromId = _clientId,
                    FromUser = _username ?? "",
                    ToId = _activePeerId!,
                    IsTyping = isTyping
                });
            }
            catch { }
        }

        // ================= SEND PACKET =================
        private void SendPacket<T>(T pkt)
        {
            var ns = _ns;
            if (ns == null) return;
            PacketIO.SendPacket(ns, pkt);
        }

        // ================= OPEN CONVO + HISTORY =================
        private void OpenConversation(string peerId, string peerName)
        {
            _activePeerId = peerId;
            _activePeerName = peerName;

            UI(() =>
            {
                lblChatTitle.Text = $"Chat with {peerName}";
                if (_convos.TryGetValue(peerId, out var c))
                    lblChatSub.Text = c.Online ? "Online" : "Offline";
                else
                    lblChatSub.Text = "";

                // ✅ FIX ngay lúc đổi text
                FixChatHeaderLayout();
            });

            if (_convos.TryGetValue(peerId, out var cc))
            {
                cc.Unread = 0;
                RefreshConvoList();
            }

            LoadHistory(peerId);
            ScrollToBottom();
        }

        // ================= HISTORY =================
        private sealed class ChatLogEntry
        {
            public long Ts { get; set; }
            public string Dir { get; set; } = "";
            public string PeerId { get; set; } = "";
            public string PeerUser { get; set; } = "";
            public string Text { get; set; } = "";
            public string MessageId { get; set; } = "";
            public string Status { get; set; } = "";
        }

        private string GetHistoryFile(string peerId)
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history", _clientId);
            Directory.CreateDirectory(root);
            return Path.Combine(root, $"{peerId}.jsonl");
        }

        private void SaveHistory(ChatLogEntry e)
        {
            try
            {
                var file = GetHistoryFile(e.PeerId);
                File.AppendAllText(file, JsonSerializer.Serialize(e) + Environment.NewLine);
            }
            catch { }
        }

        private void LoadHistory(string peerId)
        {
            UI(() =>
            {
                chatFlow.SuspendLayout();
                chatFlow.Controls.Clear();

                var file = GetHistoryFile(peerId);
                if (!File.Exists(file))
                {
                    chatFlow.ResumeLayout();
                    return;
                }

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ChatLogEntry? e = null;
                    try { e = JsonSerializer.Deserialize<ChatLogEntry>(line); }
                    catch { continue; }
                    if (e == null) continue;

                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(e.Ts).LocalDateTime;
                    bool mine = e.Dir == "out";

                    string who = mine ? "Me" : e.PeerUser;
                    string status = mine ? "✓" : "";
                    if (!string.IsNullOrEmpty(e.Status) && !mine)
                        status = e.Status;

                    AddBubble(mine, who, e.Text, dt, status, e.MessageId);
                }

                chatFlow.ResumeLayout();
            });
        }

        // ================= CHAT BUBBLES =================
        private sealed class BubbleRow : Panel
        {
            private readonly Panel _bubble;
            private readonly Label _lblWho;
            private readonly Label _lblText;
            private readonly Label _lblMeta;

            public bool IsMine { get; private set; }
            public string MessageId { get; private set; } = "";

            public BubbleRow(bool isMine, string who, string text, DateTime ts, string status, string messageId)
            {
                IsMine = isMine;
                MessageId = messageId ?? "";

                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                Dock = DockStyle.Top;
                BackColor = Color.Transparent;
                Padding = new Padding(0, 2, 0, 2); // sát hơn

                _bubble = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(12, 8, 12, 8),
                    BackColor = isMine ? Color.FromArgb(219, 236, 255) : Color.White,
                };

                _bubble.Paint += (_, e) =>
                {
                    using var pen = new Pen(Color.FromArgb(230, 230, 235));
                    var r = _bubble.ClientRectangle;
                    r.Width -= 1; r.Height -= 1;
                    e.Graphics.DrawRectangle(pen, r);
                };

                _lblWho = new Label
                {
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9.4f, FontStyle.Bold),
                    Text = who,
                };

                _lblText = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(520, 0),
                    Font = new Font("Segoe UI", 10.2f, FontStyle.Regular),
                    Text = text,
                };

                _lblMeta = new Label
                {
                    AutoSize = true,
                    ForeColor = Color.Gray,
                    Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                    Text = $"{ts:HH:mm}   {status}".Trim(),
                    Margin = new Padding(0, 4, 0, 0),
                };

                var stack = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                };

                stack.Controls.Add(_lblWho);
                stack.Controls.Add(_lblText);
                stack.Controls.Add(_lblMeta);

                _bubble.Controls.Add(stack);
                Controls.Add(_bubble);

                Resize += (_, __) => Reposition();
            }

            public void SetStatus(string status)
            {
                var parts = _lblMeta.Text.Split(new[] { "   " }, StringSplitOptions.None);
                var time = parts.Length > 0 ? parts[0].Trim() : "";
                _lblMeta.Text = string.IsNullOrEmpty(status) ? time : $"{time}   {status}";
            }

            public void Reposition()
            {
                if (Parent == null) return;

                int maxBubbleW = Math.Min(560, Parent.ClientSize.Width - 120);
                if (maxBubbleW < 260) maxBubbleW = Math.Max(200, Parent.ClientSize.Width - 40);

                foreach (Control c in _bubble.Controls)
                {
                    if (c is FlowLayoutPanel fl)
                    {
                        foreach (Control cc in fl.Controls)
                        {
                            if (cc is Label lab && lab != _lblWho && lab != _lblMeta)
                                lab.MaximumSize = new Size(maxBubbleW, 0);
                        }
                    }
                }

                _bubble.PerformLayout();

                int paddingSide = 10;
                int x = IsMine
                    ? Math.Max(paddingSide, Parent.ClientSize.Width - _bubble.Width - paddingSide - SystemInformation.VerticalScrollBarWidth)
                    : paddingSide;

                _bubble.Location = new Point(x, 0);
            }

            protected override void OnParentChanged(EventArgs e)
            {
                base.OnParentChanged(e);
                Reposition();
            }
        }

        private void AddBubble(bool isMine, string who, string text, DateTime ts, string status, string? messageId = null)
        {
            UI(() =>
            {
                var row = new BubbleRow(isMine, who, text, ts, status, messageId ?? "");
                chatFlow.Controls.Add(row);

                row.Width = chatFlow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
                row.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

                row.Reposition();
                ScrollToBottom();
            });
        }

        private void ReflowBubbles()
        {
            foreach (Control ctrl in chatFlow.Controls)
            {
                if (ctrl is BubbleRow br)
                {
                    br.Width = chatFlow.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
                    br.Reposition();
                }
            }
        }

        private void UpdateLastMineBubbleStatus(string messageId, string stage)
        {
            // Bạn muốn chỉ 1 dấu ✓ khi người kia đã nhận
            string s = stage switch
            {
                "offline_saved" => "⌛", // vẫn giữ đồng hồ nếu lưu offline
                "timeout" => "!",       // nếu bạn có stage timeout thì hiện !
                "accepted" => "✓",
                "delivered" => "✓",
                "delivered_to_client" => "✓",
                _ => "…"
            };

            for (int i = chatFlow.Controls.Count - 1; i >= 0; i--)
            {
                if (chatFlow.Controls[i] is BubbleRow br && br.IsMine && br.MessageId == messageId)
                {
                    br.SetStatus(s);
                    br.Reposition();
                    break;
                }
            }

            // Nếu muốn status line vẫn hiện khi delivered_to_client thì giữ,
            // còn không thì bạn có thể xóa đoạn dưới.
            if (stage == "delivered_to_client")
                SetStatus("Delivered to client.", muted: false);
        }


        private void ScrollToBottom()
        {
            if (chatFlow.Controls.Count == 0) return;
            var last = chatFlow.Controls[chatFlow.Controls.Count - 1];
            chatFlow.ScrollControlIntoView(last);
        }

        // ================= UTIL =================
        private string TrimPreview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 40 ? text.Substring(0, 40) + "…" : text;
        }

        private void UI(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private void Header_Resize(object? sender, EventArgs e)
        {
            // giữ status label luôn rộng, không bị cắt
            lblStatus.Width = Math.Max(200, header.ClientSize.Width - lblStatus.Left - 10);
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

        private void lvConvos_SelectedIndexChanged(object sender, EventArgs e)
        {
        }
    }
}
