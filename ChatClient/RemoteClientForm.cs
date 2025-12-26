using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatClient
{
    public partial class RemoteClientForm : Form
    {
        TcpClient? _tcp;
        StreamReader? _reader;
        StreamWriter? _writer;
        Thread? _recvThread;

        // ===== IDENTITY =====
        readonly string _clientId = Guid.NewGuid().ToString();
        string? _username;

        string _pubXml = "", _privXml = "";

        // clientId -> publicKey
        readonly Dictionary<string, string> _pubCache = new();
        readonly Dictionary<string, TaskCompletionSource<string>> _pubWaiters = new();

        // clientId -> username (for display)
        readonly Dictionary<string, string> _userNames = new();

        System.Windows.Forms.Timer _typingTimer;
        string? _typingUser;

        public RemoteClientForm()
        {
            InitializeComponent();

            CryptoHelper.GenerateRsaKeys(out _pubXml, out _privXml);

            Directory.CreateDirectory("keys");
            File.WriteAllText("keys/my_pub.xml", _pubXml);
            File.WriteAllText("keys/my_priv.xml", _privXml);

            _typingTimer = new System.Windows.Forms.Timer();
            _typingTimer.Interval = 1500;
            _typingTimer.Tick += (s, e) =>
            {
                ClearTyping();
                _typingTimer.Stop();
            };

            txtMessage.TextChanged += TxtMessage_TextChanged;
        }

        // ================= CONNECT =================
        private void btnConnect_Click(object sender, EventArgs e)
        {
            _username = txtUser.Text.Trim();
            if (string.IsNullOrEmpty(_username)) return;

            _tcp = new TcpClient();
            _tcp.Connect(txtServerIP.Text.Trim(), int.Parse(txtPort.Text));

            var ns = _tcp.GetStream();
            _reader = new StreamReader(ns);
            _writer = new StreamWriter(ns) { AutoFlush = true };

            Send(new
            {
                type = "register",
                clientId = _clientId,
                user = _username,
                publicKey = _pubXml
            });

            _recvThread = new Thread(ReceiveLoop) { IsBackground = true };
            _recvThread.Start();

            UI(() => rtbChat.AppendText("[Connected]\n"));
        }

        // ================= RECEIVE =================
        void ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    var line = _reader?.ReadLine();
                    if (line == null) break;

                    var root = JsonDocument.Parse(line).RootElement;
                    var type = root.GetProperty("type").GetString();

                    switch (type)
                    {
                        case "userlist":
                            HandleUserList(root);
                            break;

                        case "publickey":
                            HandlePubKey(root);
                            break;

                        case "typing":
                            HandleTyping(root);
                            break;

                        case "chat":
                            HandleChat(root);
                            break;

                        case "chat_ack":
                            HandleChatAck(root);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                UI(() => rtbChat.AppendText("[ERROR] " + ex.Message + "\n"));
            }
        }

        // ================= USER LIST =================
        void HandleUserList(JsonElement root)
        {
            UI(() =>
            {
                lstUsers.Items.Clear();
                _userNames.Clear();

                foreach (var u in root.GetProperty("users").EnumerateArray())
                {
                    var id = u.GetProperty("clientId").GetString();
                    var name = u.GetProperty("user").GetString();

                    if (id == null || name == null || id == _clientId)
                        continue;

                    _userNames[id] = name;
                    lstUsers.Items.Add(name);
                }
            });
        }

        // ================= PUBLIC KEY =================
        void HandlePubKey(JsonElement root)
        {
            var id = root.GetProperty("clientId").GetString();
            var key = root.GetProperty("publicKey").GetString();

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key))
                return; // ❗ BỎ QUA nếu key null

            _pubCache[id] = key;

            if (_pubWaiters.TryGetValue(id, out var tcs))
            {
                tcs.TrySetResult(key);
                _pubWaiters.Remove(id);
            }
        }

        async Task<string?> EnsurePubKeyAsync(string clientId)
        {
            if (_pubCache.TryGetValue(clientId, out var key))
                return key;

            if (!_pubWaiters.ContainsKey(clientId))
            {
                _pubWaiters[clientId] = new TaskCompletionSource<string>();
                Send(new
                {
                    type = "getpublickey",
                    clientId
                });
            }

            var task = _pubWaiters[clientId].Task;
            var done = await Task.WhenAny(task, Task.Delay(3000));
            return done == task ? task.Result : null;
        }

        // ================= CHAT =================
        async void HandleChat(JsonElement root)
        {
            var fromId = root.GetProperty("fromId").GetString();
            var fromUser = root.GetProperty("fromUser").GetString();
            var encKey = root.GetProperty("encKey").GetString();
            var encMsg = root.GetProperty("encMsg").GetString();
            var sig = root.GetProperty("sig").GetString();

            if (fromId == null || fromUser == null ||
                encKey == null || encMsg == null || sig == null)
                return;

            var pub = await EnsurePubKeyAsync(fromId);
            if (pub == null) return;

            var aesB64 = CryptoHelper.RsaDecryptBase64(encKey, _privXml);
            var aes = Convert.FromBase64String(aesB64);
            var plain = CryptoHelper.AesDecryptFromBase64(encMsg, aes);

            bool ok = CryptoHelper.VerifySignature(plain, sig, pub);

            UI(() =>
            {
                rtbChat.AppendText(ok
                    ? $"{fromUser}: {plain}\n"
                    : $"[INVALID SIGNATURE]\n");
            });
        }

        // ================= SEND =================
        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (lstUsers.SelectedItem == null)
                return;

            var toUser = lstUsers.SelectedItem.ToString()!;
            var toId = GetClientIdByName(toUser);
            if (toId == null)
                return;

            var plain = txtMessage.Text.Trim();
            if (plain.Length == 0)
                return;

            var pub = await EnsurePubKeyAsync(toId);
            if (pub == null)
            {
                MessageBox.Show("Cannot get public key");
                return;
            }

            var aes = CryptoHelper.GenerateAesKey();
            var encMsg = CryptoHelper.AesEncryptToBase64(plain, aes);
            var encKey = CryptoHelper.RsaEncryptBase64(
                Convert.ToBase64String(aes), pub);
            var sig = CryptoHelper.SignData(plain, _privXml);

            Send(new
            {
                type = "chat",
                fromId = _clientId,
                fromUser = _username,
                toId,
                toUser,
                encKey,
                encMsg,
                sig
            });

            UI(() => rtbChat.AppendText($"Me → {toUser}: {plain}\n"));
            txtMessage.Clear();
        }

        string? GetClientIdByName(string name)
        {
            foreach (var kv in _userNames)
                if (kv.Value == name)
                    return kv.Key;
            return null;
        }

        // ================= TYPING =================
        void TxtMessage_TextChanged(object? sender, EventArgs e)
        {
            if (lstUsers.SelectedItem == null) return;

            var toId = GetClientIdByName(lstUsers.SelectedItem.ToString()!);
            if (toId == null) return;

            Send(new
            {
                type = "typing",
                fromId = _clientId,
                fromUser = _username,
                toId,
                isTyping = true
            });

            _typingTimer.Stop();
            _typingTimer.Start();
        }

        void HandleTyping(JsonElement root)
        {
            var fromUser = root.GetProperty("fromUser").GetString();
            var isTyping = root.GetProperty("isTyping").GetBoolean();

            if (fromUser == null) return;

            UI(() =>
            {
                ClearTyping();
                if (isTyping)
                {
                    _typingUser = fromUser;
                    rtbChat.AppendText($"[{fromUser} is typing...]\n");
                }
            });
        }

        void ClearTyping()
        {
            if (_typingUser == null) return;
            rtbChat.Text = rtbChat.Text.Replace(
                $"[{_typingUser} is typing...]\n", "");
            _typingUser = null;
        }

        // ================= HELPERS =================
        void Send(object obj)
        {
            _writer?.WriteLine(JsonSerializer.Serialize(obj));
        }

        void UI(Action a)
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private void btnGetPubKey_Click(object sender, EventArgs e) { }
        private void rtbChat_TextChanged(object sender, EventArgs e) { }

        //HandlechatACK
        void HandleChatAck(JsonElement root)
        {
            var status = root.GetProperty("status").GetString();

            UI(() =>
            {
                if (status == "delivered")
                    rtbChat.AppendText("[✓ Delivered]\n");
                else if (status == "offline")
                    rtbChat.AppendText("[⚠ User offline – saved]\n");
                else
                    rtbChat.AppendText("[✗ Send failed]\n");
            });
        }

    }
}
