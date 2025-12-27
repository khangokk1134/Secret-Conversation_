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


        string _clientId = "";
        string? _username;

        string _pubXml = "", _privXml = "";

        readonly Dictionary<string, string> _pubCache = new();
        readonly Dictionary<string, TaskCompletionSource<string>> _pubWaiters = new();

        // clientId -> username
        readonly Dictionary<string, string> _userNames = new();

        // ===== message tracking (PRO) =====
        readonly Dictionary<string, string> _sentMessagePreview = new(); // messageId -> preview

        System.Windows.Forms.Timer _typingTimer;
        string? _typingUser;

        public RemoteClientForm()
        {
            InitializeComponent();

            Directory.CreateDirectory("keys");

            // ===== LOAD OR CREATE RSA KEY (STABLE) =====
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

            this.FormClosing += RemoteClientForm_FormClosing;
        }

        // ================= CONNECT =================
        private void btnConnect_Click(object sender, EventArgs e)
        {
            _username = txtUser.Text.Trim();
            if (string.IsNullOrEmpty(_username)) return;

            _clientId = LoadOrCreateClientId(_username);

            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(txtServerIP.Text.Trim(), int.Parse(txtPort.Text));
                _ns = _tcp.GetStream();

                SendPacket(new RegisterPacket
                {
                    ClientId = _clientId,
                    User = _username,
                    PublicKey = _pubXml
                });

                _recvThread = new Thread(ReceiveLoop) { IsBackground = true };
                _recvThread.Start();

                UI(() => rtbChat.AppendText("[Connected]\n"));
            }
            catch (Exception ex)
            {
                UI(() => rtbChat.AppendText("[ERROR] " + ex.Message + "\n"));
            }
        }

        // ================= RECEIVE =================
        void ReceiveLoop()
        {
            try
            {
                while (!_closing)
                {
                    if (_ns == null) break;

                    string? json = null;
                    try
                    {
                        json = PacketIO.ReadJson(_ns);
                    }
                    catch
                    {
                        if (_closing) break;   // đang đóng thì im lặng thoát
                        throw;                  // không đóng mà lỗi thì ném ra để log
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
                        case PacketType.Typing:
                            HandleTyping(JsonSerializer.Deserialize<TypingPacket>(json)!);
                            break;
                        case PacketType.Recall:
                            HandleRecall(JsonSerializer.Deserialize<RecallPacket>(json)!);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_closing)
                    UI(() => rtbChat.AppendText("[ERROR] " + ex.Message + "\n"));
            }
        }

        // ================= USER LIST =================
        void HandleUserList(UserListPacket pkt)
        {
            UI(() =>
            {
                lstUsers.Items.Clear();
                _userNames.Clear();

                foreach (var u in pkt.Users)
                {
                    if (u.ClientId == _clientId) continue;

                    _userNames[u.ClientId] = u.User;
                    lstUsers.Items.Add(u.Online ? u.User : $"{u.User} (offline)");
                }
            });
        }

        // ================= PUBLIC KEY =================
        void HandlePubKey(PublicKeyPacket pkt)
        {
            if (string.IsNullOrEmpty(pkt.ClientId) || string.IsNullOrEmpty(pkt.PublicKey))
                return;

            _pubCache[pkt.ClientId] = pkt.PublicKey;

            lock (_pubWaiters)
            {
                if (_pubWaiters.TryGetValue(pkt.ClientId, out var tcs))
                {
                    tcs.TrySetResult(pkt.PublicKey);
                    _pubWaiters.Remove(pkt.ClientId);
                }
            }
        }

        // ================= ENSURE PUBLIC KEY (FIX RACE CONDITION) =================
        async Task<string?> EnsurePubKeyAsync(string clientId)
        {
            if (_pubCache.TryGetValue(clientId, out var key))
                return key;

            TaskCompletionSource<string> tcs;

            lock (_pubWaiters)
            {
                if (!_pubWaiters.TryGetValue(clientId, out tcs!))
                {
                    tcs = new TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    _pubWaiters[clientId] = tcs;

                    SendPacket(new GetPublicKeyPacket
                    {
                        ClientId = clientId
                    });
                }
            }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            if (done != tcs.Task) return null;

            return await tcs.Task;
        }

        // ================= CHAT RECEIVE =================
        async void HandleChat(ChatPacket pkt)
        {
            var pub = await EnsurePubKeyAsync(pkt.FromId);
            if (pub == null) return;

            var aesB64 = CryptoHelper.RsaDecryptBase64(pkt.EncKey, _privXml);
            var aes = Convert.FromBase64String(aesB64);
            var plain = CryptoHelper.AesDecryptFromBase64(pkt.EncMsg, aes);

            bool ok = CryptoHelper.VerifySignature(plain, pkt.Sig, pub);

            UI(() =>
            {
                rtbChat.AppendText(ok
                    ? $"{pkt.FromUser}: {plain}\n"
                    : "[INVALID SIGNATURE]\n");
            });
        }

        // ================= SEND CHAT (PRO) =================
        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (lstUsers.SelectedItem == null) return;

            var display = lstUsers.SelectedItem.ToString()!;
            var toId = GetClientIdByName(display);
            if (toId == null) return;

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

            SendPacket(new ChatPacket
            {
                MessageId = msgId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FromId = _clientId,
                FromUser = _username!,
                ToId = toId,
                ToUser = display.Replace(" (offline)", ""),
                EncKey = encKey,
                EncMsg = encMsg,
                Sig = sig
            });

            _sentMessagePreview[msgId] = $"Me → {display}: {plain}";
            UI(() => rtbChat.AppendText($"Me → {display}: {plain}\n"));
            txtMessage.Clear();
        }

        // ================= ACK =================
        void HandleChatAck(ChatAckPacket pkt)
        {
            UI(() =>
            {
                var text = _sentMessagePreview.TryGetValue(pkt.MessageId, out var p)
                    ? p
                    : "(unknown message)";

                if (pkt.Status == "delivered")
                    rtbChat.AppendText($"[✓ Delivered] {text}\n");
                else if (pkt.Status == "offline")
                    rtbChat.AppendText($"[⚠ Offline – saved] {text}\n");
                else
                    rtbChat.AppendText($"[✗ Failed] {text}\n");
            });
        }

        // ================= RECALL =================
        void HandleRecall(RecallPacket pkt)
        {
            UI(() => rtbChat.AppendText($"[Message recalled] id={pkt.MessageId}\n"));
        }

        // ================= TYPING =================
        void TxtMessage_TextChanged(object? sender, EventArgs e)
        {
            if (lstUsers.SelectedItem == null) return;
            var toId = GetClientIdByName(lstUsers.SelectedItem.ToString()!);
            if (toId == null) return;

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

        // ================= HELPERS =================
        void SendPacket<T>(T pkt)
        {
            if (_ns == null) return;
            PacketIO.SendPacket(_ns, pkt);
        }

        string? GetClientIdByName(string display)
        {
            var name = display.Replace(" (offline)", "").Trim();
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

            // 1) gửi logout (best effort)
            try
            {
                if (_ns != null)
                    SendPacket(new LogoutPacket { ClientId = _clientId });
            }
            catch { }

            // 2) đóng socket để unblock thread đang Read
            try { _tcp?.Close(); } catch { }
        }

        // ===== Designer stubs =====
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
    }
}
