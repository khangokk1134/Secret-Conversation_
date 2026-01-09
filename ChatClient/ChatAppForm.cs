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

        // incoming idempotent (runtime)
        private readonly HashSet<string> _seenIncoming = new();
        private readonly object _seenLock = new();

        // pending resend + stage (1-1 only)
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

        // ✅ for Seen: remember latest incoming msgId per peer (1-1 only)
        private readonly Dictionary<string, string> _lastIncomingMsgId = new();

        // ✅ prevent spamming seen for same peer+msgId
        private readonly Dictionary<string, string> _lastSeenSent = new();

        private System.Windows.Forms.Timer _resendTimer = null!;
        private System.Windows.Forms.Timer _typingDebounce = null!;

        // Active convo key:
        // - user: clientId
        // - room: "room:<roomId>"
        private string? _activePeerId;
        private string? _activePeerName;

        // ================= GROUP =====
        private sealed class RoomState
        {
            public string RoomId = "";
            public string RoomName = "";
            public string[] Members = Array.Empty<string>();
        }
        private readonly Dictionary<string, RoomState> _rooms = new();

        private bool IsRoomKey(string? key) => !string.IsNullOrEmpty(key) && key.StartsWith("room:", StringComparison.Ordinal);
        private string RoomIdFromKey(string key) => key.Substring("room:".Length);
        private string MakeRoomKey(string roomId) => "room:" + roomId;

        // ================= DATA MODEL (CONVOS) =================
        private sealed class ConvoState
        {
            public string PeerId = ""; // can be clientId or room:<id>
            public string Name = "";
            public string Last = "";
            public int Unread = 0;
            public bool Online = false;
            public long LastTs = 0;
        }

        private readonly Dictionary<string, ConvoState> _convos = new();  // peerId -> state
        private readonly Dictionary<string, string> _userNames = new();   // clientId -> username

        // column resize guard
        private bool _fixingColumns = false;

        // when we update list programmatically, prevent selection recursion
        private bool _updatingConvoList = false;

        // ================= PIN / DELETE / LEAVE =================
        private readonly HashSet<string> _pinned = new HashSet<string>(StringComparer.Ordinal);

        // ✅ Context menu instance (Designer có thể có sẵn, nhưng tạo ở code để chắc)
        private ContextMenuStrip _convoMenu = null!;
        private ToolStripMenuItem _miDeleteConvo = null!;

        public ChatAppForm()
        {
            InitializeComponent();

            Text = "Secure Chat Client";
            MinimumSize = new Size(980, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

            LoadKeys();
            HookUI();
            InitConvoContextMenu();

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

            // IMPORTANT: hook resize ONCE
            chatFlow.Resize += (_, __) => ReflowBubbles();

            // keep Online under title
            Shown += (_, __) => FixChatHeaderLayout();
            Resize += (_, __) => FixChatHeaderLayout();
            lblChatTitle.SizeChanged += (_, __) => FixChatHeaderLayout();
            lblChatSub.SizeChanged += (_, __) => FixChatHeaderLayout();

            SetUiConnected(false);
            SetStatus("Enter server/port/user then click Connect.", muted: true);

            FixConvoColumns();
            FixChatHeaderLayout();

            // ✅ position "+ Group" button if exists
            try { PositionCreateGroupButton(); } catch { }
        }

        private void ChatAppForm_Load(object sender, EventArgs e)
        {
            FixChatHeaderLayout();
            try { PositionCreateGroupButton(); } catch { }
        }

        // ================= FIX: CHAT HEADER LAYOUT =================
        private void FixChatHeaderLayout()
        {
            if (IsDisposed) return;
            if (lblChatTitle == null || lblChatSub == null) return;

            UI(() =>
            {
                lblChatTitle.AutoSize = true;
                lblChatSub.AutoSize = true;

                lblChatSub.Left = lblChatTitle.Left;
                lblChatSub.Top = lblChatTitle.Bottom + 2;

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

            // ✅ Create group button (Designer có)
            if (btnCreateGroup != null)
            {
                btnCreateGroup.Click += (_, __) => CreateGroupUi();
            }
            if (leftPanel != null)
            {
                leftPanel.Resize += (_, __) => PositionCreateGroupButton();
            }

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
                if (IsRoomKey(_activePeerId)) return; // typing not implemented for rooms

                _typingDebounce.Stop();
                _typingDebounce.Start();
            };

            // Seen will happen in OpenConversation only (when user clicks a convo)
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

        // ✅ Context menu for conversation list
        private void InitConvoContextMenu()
        {
            // IMPORTANT:
            // - Designer đã có thể tạo miPin/miUnpin/miLeaveGroup (field),
            //   nên ở đây chỉ dùng lại nếu có; nếu không có thì tạo mới an toàn.

            _convoMenu = new ContextMenuStrip();
            _convoMenu.Opening += ConvoMenu_Opening;

            // miPin / miUnpin / miLeaveGroup có thể tồn tại từ Designer
            // => dùng trực tiếp, KHÔNG khai báo lại (tránh CS0102/CS0229)
            if (miPin == null) miPin = new ToolStripMenuItem("Pin");
            if (miUnpin == null) miUnpin = new ToolStripMenuItem("Unpin");
            if (miLeaveGroup == null) miLeaveGroup = new ToolStripMenuItem("Leave group");

            miPin.Click += MiPin_Click;
            miUnpin.Click += MiUnpin_Click;
            miLeaveGroup.Click += MiLeaveGroup_Click;

            _miDeleteConvo = new ToolStripMenuItem("Delete (local)");
            _miDeleteConvo.Click += MiDeleteConvo_Click;

            _convoMenu.Items.Add(miPin);
            _convoMenu.Items.Add(miUnpin);
            _convoMenu.Items.Add(new ToolStripSeparator());
            _convoMenu.Items.Add(_miDeleteConvo);
            _convoMenu.Items.Add(miLeaveGroup);

            lvConvos.ContextMenuStrip = _convoMenu;
        }

        // ✅ keep "+ Group" at top-right under Conversations title line
        private void PositionCreateGroupButton()
        {
            if (btnCreateGroup == null || leftPanel == null) return;

            btnCreateGroup.Top = 0;
            btnCreateGroup.Left = Math.Max(0, leftPanel.ClientSize.Width - btnCreateGroup.Width - 6);
        }

        // ✅ Create group UI by selecting members
        private void CreateGroupUi()
        {
            if (!IsReallyConnected())
            {
                SetStatus("Not connected.", muted: true);
                return;
            }

            var items = new List<CreateGroupForm.UserItem>();

            foreach (var kv in _convos.Values)
            {
                if (string.IsNullOrWhiteSpace(kv.PeerId)) continue;
                if (kv.PeerId == _clientId) continue;
                if (IsRoomKey(kv.PeerId)) continue;

                items.Add(new CreateGroupForm.UserItem
                {
                    ClientId = kv.PeerId,
                    Name = string.IsNullOrWhiteSpace(kv.Name) ? kv.PeerId : kv.Name,
                    Online = kv.Online
                });
            }

            foreach (var kv in _userNames)
            {
                var id = kv.Key;
                if (id == _clientId) continue;
                if (items.Any(x => x.ClientId == id)) continue;

                items.Add(new CreateGroupForm.UserItem
                {
                    ClientId = id,
                    Name = string.IsNullOrWhiteSpace(kv.Value) ? id : kv.Value,
                    Online = _convos.TryGetValue(id, out var cc) && cc.Online
                });
            }

            if (items.Count == 0)
            {
                SetStatus("No other users to add.", muted: true);
                return;
            }

            using var dlg = new CreateGroupForm(items);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var roomName = dlg.RoomName;
            if (string.IsNullOrWhiteSpace(roomName))
            {
                SetStatus("Group name is required.", muted: true);
                return;
            }

            var picked = dlg.SelectedClientIds;
            if (picked.Count < 2)
            {
                SetStatus("Pick at least 2 members (besides you).", muted: true);
                return;
            }

            var roomId = Guid.NewGuid().ToString();

            var memberIds = new List<string> { _clientId };
            memberIds.AddRange(picked);
            memberIds = memberIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

            try
            {
                SendPacket(new CreateRoomPacket
                {
                    RoomId = roomId,
                    RoomName = roomName,
                    CreatorId = _clientId,
                    MemberIds = memberIds.ToArray()
                });

                SetStatus($"Creating group: {roomName}", muted: false);
            }
            catch (Exception ex)
            {
                SetStatus("Create group failed: " + ex.Message, muted: true);
            }
        }

        // ================= FIX COLUMNS =================
        private void FixConvoColumns()
        {
            if (_fixingColumns) return;
            if (lvConvos.IsDisposed) return;

            _fixingColumns = true;
            try
            {
                int w = lvConvos.ClientSize.Width;
                if (w <= 10) return;

                int unreadW = 95;
                int lastW = 100;
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

                if (btnCreateGroup != null) btnCreateGroup.Enabled = connected;
            });
        }

        // ================= KEYS + CLIENTID =================
        private void LoadKeys()
        {
            var keyRoot = Path.Combine(DataRoot(), "keys");
            Directory.CreateDirectory(keyRoot);
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

            // show old conversations even before server userlist arrives
            LoadPinned();
            LoadConvosFromHistory();
            RefreshConvoList();

            try
            {
                _closing = false;

                _tcp = new TcpClient();
                _tcp.Connect(txtServer.Text.Trim(), int.Parse(txtPort.Text.Trim()));
                _ns = _tcp.GetStream();

                _connected = true;
                SetUiConnected(true);
                SetStatus("Connected.", muted: false);

                UI(() =>
                {
                    chatFlow.Controls.Clear();
                    lblChatTitle.Text = "Chat";
                    lblChatSub.Text = "";
                });

                _rooms.Clear();
                _userNames.Clear();
                _lastIncomingMsgId.Clear();
                _lastSeenSent.Clear();

                lock (_pendingLock) { _pending.Clear(); }

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
            LoadPinned();
            _rooms.Clear();
            _userNames.Clear();
            _lastIncomingMsgId.Clear();
            _lastSeenSent.Clear();

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

                        // ===== GROUP =====
                        case PacketType.RoomInfo:
                            HandleRoomInfo(JsonSerializer.Deserialize<RoomInfoPacket>(json)!);
                            break;

                        case PacketType.RoomChat:
                            HandleRoomChat(JsonSerializer.Deserialize<RoomChatPacket>(json)!);
                            break;

                        case PacketType.RoomAck:
                            HandleRoomAck(JsonSerializer.Deserialize<RoomAckPacket>(json)!);
                            break;

                        // ✅ NEW: remove room from client list (when user leaves)
                        case PacketType.RoomInfoRemoved:
                            HandleRoomInfoRemoved(JsonSerializer.Deserialize<RoomInfoRemovedPacket>(json)!);
                            break;

                        case PacketType.Typing:
                            break;

                        case PacketType.Recall:
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
                if (!string.IsNullOrEmpty(_activePeerId))
                {
                    if (_convos.TryGetValue(_activePeerId, out var cur))
                        lblChatSub.Text = IsRoomKey(_activePeerId) ? "" : (cur.Online ? "Online" : "Offline");
                }
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
                        .OrderByDescending(x => IsPinnedPeer(x.PeerId))
                        .ThenByDescending(x => x.LastTs)
                        .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        string name;
                        if (IsRoomKey(c.PeerId))
                        {
                            name = c.Name;
                        }
                        else
                        {
                            name = c.Name + (c.Online ? "" : " (offline)");
                        }

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

        // ================= CHAT RECEIVE (1-1) =================
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
            catch { return; }

            // prevent duplicates across restarts (offline re-delivery)
            if (HistoryContainsMessageId(pkt.FromId, pkt.MessageId!))
            {
                SendReceipt(pkt);
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

            _lastIncomingMsgId[pkt.FromId] = pkt.MessageId!;

            if (_activePeerId == pkt.FromId)
            {
                AddBubble(isMine: false, who: c.Name, text: plain, ts: DateTime.Now, status: verified ? "" : "UNVERIFIED");
                SendSeenIfAny(pkt.FromId);
            }
        }

        // unify receipt meaning to delivered_to_client
        private void SendReceipt(ChatPacket pkt)
        {
            try
            {
                if (!IsReallyConnected()) return;

                SendPacket(new DeliveryReceiptPacket
                {
                    MessageId = pkt.MessageId ?? "",
                    FromId = _clientId,   // receiver
                    ToId = pkt.FromId,    // sender
                    Status = "delivered_to_client",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            catch { }
        }

        // Seen is sent when user opens the conversation (clicks it)
        private void SendSeenIfAny(string peerId)
        {
            try
            {
                if (!IsReallyConnected()) return;
                if (string.IsNullOrEmpty(peerId)) return;
                if (IsRoomKey(peerId)) return;

                if (_lastIncomingMsgId.TryGetValue(peerId, out var mid) && !string.IsNullOrEmpty(mid))
                {
                    if (_lastSeenSent.TryGetValue(peerId, out var sent) && sent == mid)
                        return;

                    _lastSeenSent[peerId] = mid;

                    SendPacket(new SeenReceiptPacket
                    {
                        MessageId = mid,
                        FromId = _clientId, // viewer
                        ToId = peerId,      // sender
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
            }
            catch { }
        }

        // ================= GROUP: ROOM INFO =================
        private void HandleRoomInfo(RoomInfoPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.RoomId) || string.IsNullOrEmpty(pkt.RoomName)) return;

            _rooms[pkt.RoomId] = new RoomState
            {
                RoomId = pkt.RoomId,
                RoomName = pkt.RoomName,
                Members = pkt.MemberIds ?? Array.Empty<string>()
            };

            var key = MakeRoomKey(pkt.RoomId);

            if (!_convos.TryGetValue(key, out var c))
            {
                c = new ConvoState
                {
                    PeerId = key,
                    Name = "👥 " + pkt.RoomName,
                    Online = true
                };
                _convos[key] = c;
            }
            else
            {
                c.Name = "👥 " + pkt.RoomName;
                c.Online = true;
            }

            RefreshConvoList();
        }

        // ✅ NEW: server says remove this room from my list
        private void HandleRoomInfoRemoved(RoomInfoRemovedPacket pkt)
        {
            if (pkt == null || string.IsNullOrEmpty(pkt.RoomId)) return;

            _rooms.Remove(pkt.RoomId);

            var key = MakeRoomKey(pkt.RoomId);
            _convos.Remove(key);

            if (_activePeerId == key)
            {
                _activePeerId = null;
                _activePeerName = null;

                UI(() =>
                {
                    chatFlow.Controls.Clear();
                    lblChatTitle.Text = "Chat";
                    lblChatSub.Text = "";
                    FixChatHeaderLayout();
                });
            }

            RefreshConvoList();
            SetStatus("You left the group.", muted: false);
        }

        // ================= GROUP: ROOM CHAT (E2E) =================
        private async void HandleRoomChat(RoomChatPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.RoomId) ||
                string.IsNullOrEmpty(pkt.FromId) ||
                string.IsNullOrEmpty(pkt.MessageId))
                return;

            // ===== dedup runtime (same run) =====
            var idem = $"room:{pkt.RoomId}:{pkt.FromId}:{pkt.MessageId}";
            lock (_seenLock)
            {
                if (_seenIncoming.Contains(idem)) return;
                _seenIncoming.Add(idem);
            }

            // ===== Build room key for convo/history =====
            var key = MakeRoomKey(pkt.RoomId);

            // ✅ IMPORTANT:
            // If message already exists in history (app restart/offline replay),
            // we MUST still send receipt (so server can remove offline),
            // but should NOT render/store again.
            bool alreadyInHistory = HistoryContainsMessageId(key, pkt.MessageId!);

            // ===== decrypt AES key for me =====
            if (pkt.EncKeys == null ||
                !pkt.EncKeys.TryGetValue(_clientId, out var encKeyForMe) ||
                string.IsNullOrEmpty(encKeyForMe))
            {
                // If clientId changed (deleted clientid txt), you can't decrypt old offline messages.
                // Just ignore.
                return;
            }

            string plain;
            try
            {
                var aesB64 = CryptoHelper.RsaDecryptBase64(encKeyForMe, _privXml);
                var aes = Convert.FromBase64String(aesB64);
                plain = CryptoHelper.AesDecryptFromBase64(pkt.EncMsg, aes);
            }
            catch
            {
                return;
            }

            // Resolve sender name (for UI + history)
            var senderName = !string.IsNullOrWhiteSpace(pkt.FromUser)
                ? pkt.FromUser
                : (_userNames.TryGetValue(pkt.FromId, out var nn) && !string.IsNullOrWhiteSpace(nn) ? nn : pkt.FromId);

            // ✅ send room delivery receipt to server (ALWAYS, even if duplicate-history)
            try
            {
                if (IsReallyConnected())
                {
                    SendPacket(new RoomDeliveryReceiptPacket
                    {
                        RoomId = pkt.RoomId,
                        MessageId = pkt.MessageId ?? "",
                        FromId = _clientId,     // receiver = me
                        ToId = pkt.FromId,      // original sender
                        Status = "delivered_to_client",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
            }
            catch { }

            // If already stored in history => don't show/store again
            if (alreadyInHistory)
                return;

            // ===== verify signature =====
            bool verified = false;
            var pub = await EnsurePubKeyAsync(pkt.FromId);
            if (!string.IsNullOrEmpty(pub))
            {
                try { verified = CryptoHelper.VerifySignature(plain, pkt.Sig, pub); }
                catch { verified = false; }
            }

            // ===== ensure convo exists =====
            if (!_convos.TryGetValue(key, out var c))
            {
                var rn = _rooms.TryGetValue(pkt.RoomId, out var rs) ? rs.RoomName : pkt.RoomId;
                c = new ConvoState
                {
                    PeerId = key,
                    Name = "👥 " + rn,
                    Online = true
                };
                _convos[key] = c;
            }

            c.Last = TrimPreview(plain);
            c.LastTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_activePeerId != key)
                c.Unread++;

            RefreshConvoList();

            // ✅ FIX: History must store senderName (NOT group name)
            SaveHistory(new ChatLogEntry
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Dir = "in",
                PeerId = key,              // room key
                PeerUser = senderName,     // ✅ sender name
                Text = plain,
                MessageId = pkt.MessageId ?? "",
                Status = verified ? "" : "UNVERIFIED"
            });

            // ===== render if active room =====
            if (_activePeerId == key)
            {
                AddBubble(
                    isMine: false,
                    who: senderName,
                    text: plain,
                    ts: DateTime.Now,
                    status: verified ? "" : "UNVERIFIED"
                );
            }
        }


        private void HandleRoomAck(RoomAckPacket pkt)
        {
            if (pkt == null) return;
            if (string.IsNullOrEmpty(pkt.MessageId)) return;

            UI(() => UpdateLastMineBubbleStatus(pkt.MessageId, pkt.Status ?? ""));
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

            if (pkt.Status == "seen")
            {
                var peer = pkt.ToId; // viewer id
                if (!string.IsNullOrEmpty(peer))
                {
                    SaveHistory(new ChatLogEntry
                    {
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Dir = "sys",
                        PeerId = peer,
                        PeerUser = _userNames.TryGetValue(peer, out var nn) ? nn : peer,
                        Text = "__seen__",
                        MessageId = pkt.MessageId,
                        Status = "seen"
                    });
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

            // Quick create group: /group RoomName
            if (plain.StartsWith("/group ", StringComparison.OrdinalIgnoreCase))
            {
                var roomName = plain.Substring(7).Trim();
                if (roomName.Length == 0) roomName = "New Group";

                if (string.IsNullOrEmpty(_activePeerId) || IsRoomKey(_activePeerId) || _activePeerId == _clientId)
                {
                    SetStatus("Open a 1-1 chat first, then use /group RoomName", muted: true);
                    return;
                }

                var roomId = Guid.NewGuid().ToString();

                SendPacket(new CreateRoomPacket
                {
                    RoomId = roomId,
                    RoomName = roomName,
                    CreatorId = _clientId,
                    MemberIds = new[] { _clientId, _activePeerId }
                });

                txtMessage.Clear();
                SetStatus($"Creating group: {roomName}", muted: false);
                return;
            }

            var toId = _activePeerId!;
            var toName = _activePeerName ?? toId;

            // ===== ROOM CHAT =====
            if (IsRoomKey(toId))
            {
                var roomId = RoomIdFromKey(toId);
                if (!_rooms.TryGetValue(roomId, out var rs))
                {
                    SetStatus("Room info not ready.", muted: true);
                    return;
                }

                var members = (rs.Members ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrEmpty(x) && x != _clientId)
                    .Distinct()
                    .ToList();

                if (members.Count == 0)
                {
                    SetStatus("Room has no other members.", muted: true);
                    return;
                }

                var msgId = Guid.NewGuid().ToString();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var aes = CryptoHelper.GenerateAesKey();
                var encMsg = CryptoHelper.AesEncryptToBase64(plain, aes);
                var aesB64 = Convert.ToBase64String(aes);

                var encKeys = new Dictionary<string, string>();
                foreach (var mid in members)
                {
                    var pub = await EnsurePubKeyAsync(mid);
                    if (string.IsNullOrEmpty(pub))
                    {
                        SetStatus("Cannot get public key for a member: " + mid, muted: true);
                        return;
                    }
                    encKeys[mid] = CryptoHelper.RsaEncryptBase64(aesB64, pub);
                }

                var sig = CryptoHelper.SignData(plain, _privXml);

                var rpkt = new RoomChatPacket
                {
                    RoomId = roomId,
                    FromId = _clientId,
                    FromUser = _username ?? "",
                    EncMsg = encMsg,
                    EncKeys = encKeys,
                    Sig = sig,
                    MessageId = msgId,
                    Timestamp = nowMs
                };

                SendPacket(rpkt);

                if (_convos.TryGetValue(toId, out var croom))
                {
                    croom.Last = TrimPreview(plain);
                    croom.LastTs = nowMs;
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
                return;
            }

            // ===== 1-1 CHAT =====
            var pub1 = await EnsurePubKeyAsync(toId);
            if (pub1 == null)
            {
                SetStatus("Cannot get receiver public key.", muted: true);
                return;
            }

            var msgId1 = Guid.NewGuid().ToString();
            var nowMs1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var aes1 = CryptoHelper.GenerateAesKey();
            var encMsg1 = CryptoHelper.AesEncryptToBase64(plain, aes1);
            var encKey1 = CryptoHelper.RsaEncryptBase64(Convert.ToBase64String(aes1), pub1);
            var sig1 = CryptoHelper.SignData(plain, _privXml);

            var pkt = new ChatPacket
            {
                MessageId = msgId1,
                Timestamp = nowMs1,
                FromId = _clientId,
                FromUser = _username ?? "",
                ToId = toId,
                ToUser = toName,
                EncKey = encKey1,
                EncMsg = encMsg1,
                Sig = sig1
            };

            lock (_pendingLock)
            {
                _pending[msgId1] = new PendingMsg
                {
                    Packet = pkt,
                    Attempts = 1,
                    FirstSentMs = nowMs1,
                    LastSentMs = nowMs1,
                    Stage = "new",
                    PlainPreview = plain
                };
            }

            SendPacket(pkt);

            if (_convos.TryGetValue(toId, out var c))
            {
                c.Last = TrimPreview(plain);
                c.LastTs = nowMs1;
                RefreshConvoList();
            }

            SaveHistory(new ChatLogEntry
            {
                Ts = nowMs1,
                Dir = "out",
                PeerId = toId,
                PeerUser = toName,
                Text = plain,
                MessageId = msgId1,
                Status = "sent"
            });

            AddBubble(isMine: true, who: "Me", text: plain, ts: DateTime.Now, status: "…", messageId: msgId1);

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
                if (IsRoomKey(_activePeerId)) return;

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
                if (IsRoomKey(peerId))
                {
                    lblChatTitle.Text = peerName;
                    lblChatSub.Text = "";
                }
                else
                {
                    lblChatTitle.Text = $"Chat with {peerName}";
                    if (_convos.TryGetValue(peerId, out var c))
                        lblChatSub.Text = c.Online ? "Online" : "Offline";
                    else
                        lblChatSub.Text = "";
                }

                FixChatHeaderLayout();
            });

            if (_convos.TryGetValue(peerId, out var cc))
            {
                cc.Unread = 0;
                RefreshConvoList();
            }

            LoadHistory(peerId);
            ScrollToBottom();

            if (!IsRoomKey(peerId))
                SendSeenIfAny(peerId);
        }

        // ================= HISTORY =================
        private sealed class ChatLogEntry
        {
            public long Ts { get; set; }
            public string Dir { get; set; } = ""; // in/out/sys
            public string PeerId { get; set; } = "";
            public string PeerUser { get; set; } = "";
            public string Text { get; set; } = "";
            public string MessageId { get; set; } = "";
            public string Status { get; set; } = "";
        }

        private string GetHistoryFile(string peerId)
        {
            var root = Path.Combine(DataRoot(), "history", _clientId);
            Directory.CreateDirectory(root);

            string safeName;
            if (IsRoomKey(peerId))
                safeName = "r_" + RoomIdFromKey(peerId);
            else
                safeName = "u_" + peerId;

            return Path.Combine(root, $"{safeName}.jsonl");
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

                string lastSeenId = "";

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    ChatLogEntry? e = null;
                    try { e = JsonSerializer.Deserialize<ChatLogEntry>(line); }
                    catch { continue; }
                    if (e == null) continue;

                    if (e.Dir == "sys" && e.Status == "seen" && !string.IsNullOrEmpty(e.MessageId))
                    {
                        lastSeenId = e.MessageId;
                        continue;
                    }

                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(e.Ts).LocalDateTime;
                    bool mine = e.Dir == "out";

                    string who = mine ? "Me" : e.PeerUser;
                    string status = mine ? "Sent" : "";
                    if (!string.IsNullOrEmpty(e.Status) && !mine)
                        status = e.Status;

                    AddBubble(mine, who, e.Text, dt, status, e.MessageId);
                }

                if (!string.IsNullOrEmpty(lastSeenId))
                    MarkSeenUpTo(lastSeenId);

                chatFlow.ResumeLayout();
            });
        }

        private void LoadConvosFromHistory()
        {
            try
            {
                if (string.IsNullOrEmpty(_clientId)) return;

                var root = Path.Combine(DataRoot(), "history", _clientId);
                if (!Directory.Exists(root)) return;

                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrEmpty(name)) continue;

                    string peerId;
                    if (name.StartsWith("u_", StringComparison.Ordinal))
                        peerId = name.Substring(2);
                    else if (name.StartsWith("r_", StringComparison.Ordinal))
                        peerId = "room:" + name.Substring(2);
                    else
                        continue;

                    long lastTs = 0;
                    string lastText = "";
                    string lastPeerUser = "";

                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var e = JsonSerializer.Deserialize<ChatLogEntry>(line);
                            if (e == null) continue;
                            if (e.Dir == "sys") continue;

                            lastTs = e.Ts;
                            lastText = TrimPreview(e.Text ?? "");
                            lastPeerUser = e.PeerUser ?? "";
                        }
                        catch { }
                    }

                    if (!_convos.TryGetValue(peerId, out var c))
                    {
                        c = new ConvoState { PeerId = peerId };
                        _convos[peerId] = c;
                    }

                    if (string.IsNullOrWhiteSpace(c.Name))
                    {
                        if (IsRoomKey(peerId))
                            c.Name = "👥 " + peerId;
                        else if (_userNames.TryGetValue(peerId, out var nn) && !string.IsNullOrWhiteSpace(nn))
                            c.Name = nn;
                        else if (!string.IsNullOrWhiteSpace(lastPeerUser))
                            c.Name = lastPeerUser;
                        else
                            c.Name = peerId;
                    }

                    c.LastTs = Math.Max(c.LastTs, lastTs);
                    if (!string.IsNullOrEmpty(lastText))
                        c.Last = lastText;
                }
            }
            catch { }
        }

        private bool HistoryContainsMessageId(string peerId, string messageId)
        {
            if (string.IsNullOrEmpty(peerId) || string.IsNullOrEmpty(messageId)) return false;

            try
            {
                var file = GetHistoryFile(peerId);
                if (!File.Exists(file)) return false;

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Contains(messageId, StringComparison.Ordinal)) continue;

                    try
                    {
                        var e = JsonSerializer.Deserialize<ChatLogEntry>(line);
                        if (e != null && e.MessageId == messageId) return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
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
                Padding = new Padding(0, 2, 0, 2);

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

        private void MarkSeenUpTo(string messageId)
        {
            int idx = -1;
            for (int i = 0; i < chatFlow.Controls.Count; i++)
            {
                if (chatFlow.Controls[i] is BubbleRow br && br.IsMine && br.MessageId == messageId)
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                for (int i = chatFlow.Controls.Count - 1; i >= 0; i--)
                {
                    if (chatFlow.Controls[i] is BubbleRow br && br.IsMine)
                    {
                        br.SetStatus("Seen");
                        br.Reposition();
                        break;
                    }
                }
                return;
            }

            for (int i = 0; i <= idx; i++)
            {
                if (chatFlow.Controls[i] is BubbleRow br && br.IsMine)
                {
                    br.SetStatus("Seen");
                    br.Reposition();
                }
            }
        }

        private void UpdateLastMineBubbleStatus(string messageId, string stage)
        {
            string s = stage switch
            {
                "offline_saved" => "Sent",
                "accepted" => "Sent",
                "delivered" => "Delivered",
                "delivered_to_client" => "Delivered",
                "timeout" => "Failed",
                _ => "…"
            };

            if (stage == "seen")
            {
                MarkSeenUpTo(messageId);
                return;
            }

            for (int i = chatFlow.Controls.Count - 1; i >= 0; i--)
            {
                if (chatFlow.Controls[i] is BubbleRow br && br.IsMine && br.MessageId == messageId)
                {
                    br.SetStatus(s);
                    br.Reposition();
                    break;
                }
            }
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

        // Designer still hooks this; keep it empty
        private void lvConvos_SelectedIndexChanged(object sender, EventArgs e) { }

        // ================= PIN / DELETE / LEAVE =================
        private string PinnedFilePath()
        {
            var root = Path.Combine(DataRoot(), "history", _clientId);
            Directory.CreateDirectory(root);
            return Path.Combine(root, "pinned.json");
        }

        private void LoadPinned()
        {
            try
            {
                _pinned.Clear();
                var f = PinnedFilePath();
                if (!File.Exists(f)) return;

                var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(f)) ?? Array.Empty<string>();
                foreach (var x in arr)
                    if (!string.IsNullOrWhiteSpace(x)) _pinned.Add(x.Trim());
            }
            catch { }
        }

        private void SavePinned()
        {
            try
            {
                File.WriteAllText(PinnedFilePath(), JsonSerializer.Serialize(_pinned.ToArray()));
            }
            catch { }
        }

        private bool IsPinnedPeer(string peerId) => !string.IsNullOrEmpty(peerId) && _pinned.Contains(peerId);

        private ConvoState? GetSelectedConvo()
        {
            if (lvConvos.SelectedItems.Count == 0) return null;
            return lvConvos.SelectedItems[0].Tag as ConvoState;
        }

        private void ConvoMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var c = GetSelectedConvo();
            if (c == null)
            {
                e.Cancel = true;
                return;
            }

            bool isPinned = IsPinnedPeer(c.PeerId);
            miPin.Visible = !isPinned;
            miUnpin.Visible = isPinned;

            bool isRoom = IsRoomKey(c.PeerId);
            miLeaveGroup.Visible = isRoom;
        }

        private void MiPin_Click(object? sender, EventArgs e)
        {
            var c = GetSelectedConvo();
            if (c == null) return;

            _pinned.Add(c.PeerId);
            SavePinned();
            RefreshConvoList();
        }

        private void MiUnpin_Click(object? sender, EventArgs e)
        {
            var c = GetSelectedConvo();
            if (c == null) return;

            _pinned.Remove(c.PeerId);
            SavePinned();
            RefreshConvoList();
        }

        private void MiDeleteConvo_Click(object? sender, EventArgs e)
        {
            var c = GetSelectedConvo();
            if (c == null) return;

            var peerId = c.PeerId;

            try
            {
                var file = GetHistoryFile(peerId);
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }

            _pinned.Remove(peerId);
            SavePinned();

            _convos.Remove(peerId);

            if (_activePeerId == peerId)
            {
                _activePeerId = null;
                _activePeerName = null;

                UI(() =>
                {
                    chatFlow.Controls.Clear();
                    lblChatTitle.Text = "Chat";
                    lblChatSub.Text = "";
                    FixChatHeaderLayout();
                });
            }

            RefreshConvoList();
            SetStatus("Deleted conversation (local).", muted: false);
        }

        // ✅ Leave group: send LeaveRoomPacket to server
        private void MiLeaveGroup_Click(object? sender, EventArgs e)
        {
            var c = GetSelectedConvo();
            if (c == null) return;
            if (!IsRoomKey(c.PeerId)) return;

            var roomId = RoomIdFromKey(c.PeerId);

            try
            {
                if (IsReallyConnected())
                {
                    SendPacket(new LeaveRoomPacket
                    {
                        RoomId = roomId,
                        ClientId = _clientId
                    });
                }
            }
            catch { }

            // UI will be removed when RoomInfoRemoved arrives from server
            SetStatus("Leaving group...", muted: false);
        }
        private static string DataRoot()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SecureChat");
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
