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

        string? _username;
        string _pubXml = "", _privXml = "";

        readonly Dictionary<string, string> _pubCache = new();
        readonly Dictionary<string, TaskCompletionSource<string>> _pubWaiters = new();

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
            if (_username.Length == 0) return;

            _tcp = new TcpClient();
            _tcp.Connect(txtServerIP.Text.Trim(), int.Parse(txtPort.Text));
            var ns = _tcp.GetStream();

            _reader = new StreamReader(ns);
            _writer = new StreamWriter(ns) { AutoFlush = true };

            Send(new
            {
                type = "register",
                user = _username,
                pubkey = _pubXml
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
                            UI(() =>
                            {
                                lstUsers.Items.Clear();
                                foreach (var u in root.GetProperty("users").EnumerateArray())
                                {
                                    var name = u.GetString();
                                    if (!string.IsNullOrEmpty(name) && name != _username)
                                        lstUsers.Items.Add(name);
                                }
                            });
                            break;

                        case "pubkey":
                            HandlePubKey(root);
                            break;

                        case "typing":
                            HandleTyping(root);
                            break;

                        case "chat":
                            HandleChat(root);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                UI(() => rtbChat.AppendText("[ERROR] " + ex.Message + "\n"));
            }
        }

        // ================= PUBKEY =================
        void HandlePubKey(JsonElement root)
        {
            var user = root.GetProperty("user").GetString();
            var key = root.GetProperty("pubkey").GetString();
            if (user == null || key == null) return;

            _pubCache[user] = key;

            if (_pubWaiters.TryGetValue(user, out var tcs))
            {
                tcs.TrySetResult(key);
                _pubWaiters.Remove(user);
            }

            UI(() => rtbChat.AppendText($"[Got pubkey of {user}]\n"));
        }

        async Task<string?> EnsurePubKeyAsync(string user)
        {
            if (_pubCache.TryGetValue(user, out var key))
                return key;

            if (!_pubWaiters.ContainsKey(user))
            {
                _pubWaiters[user] = new TaskCompletionSource<string>();
                Send(new { type = "get_pubkey", user });
            }

            var task = _pubWaiters[user].Task;
            var done = await Task.WhenAny(task, Task.Delay(2000));
            return done == task ? task.Result : null;
        }

        // ================= CHAT =================
        async void HandleChat(JsonElement root)
        {
            var from = root.GetProperty("from").GetString()!;
            var encKey = root.GetProperty("encKey").GetString()!;
            var encMsg = root.GetProperty("encMsg").GetString()!;
            var sig = root.GetProperty("sig").GetString()!;

            var pub = await EnsurePubKeyAsync(from);
            if (pub == null)
            {
                UI(() => rtbChat.AppendText($"[Missing pubkey of {from}]\n"));
                return;
            }

            var aesB64 = CryptoHelper.RsaDecryptBase64(encKey, _privXml);
            var aes = Convert.FromBase64String(aesB64);
            var plain = CryptoHelper.AesDecryptFromBase64(encMsg, aes);

            bool ok = CryptoHelper.VerifySignature(plain, sig, pub);

            UI(() =>
            {
                rtbChat.AppendText(ok
                    ? $"{from}: {plain}\n"
                    : $"[INVALID SIGNATURE from {from}]\n");
            });
        }

        // ================= SEND =================
        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (_writer == null || lstUsers.SelectedItem == null) return;

            var to = lstUsers.SelectedItem.ToString()!;
            var plain = txtMessage.Text.Trim();
            if (plain.Length == 0) return;

            var pub = await EnsurePubKeyAsync(to);
            if (pub == null)
            {
                MessageBox.Show($"Cannot get public key of {to}");
                return;
            }

            var aes = CryptoHelper.GenerateAesKey();
            var encMsg = CryptoHelper.AesEncryptToBase64(plain, aes);
            var encKey = CryptoHelper.RsaEncryptBase64(Convert.ToBase64String(aes), pub);
            var sig = CryptoHelper.SignData(plain, _privXml);

            Send(new
            {
                type = "chat",
                from = _username,
                to,
                encKey,
                encMsg,
                sig
            });

            UI(() => rtbChat.AppendText($"Me → {to}: {plain}\n"));
            txtMessage.Clear();
            ClearTyping();
        }

        // ================= TYPING =================
        void TxtMessage_TextChanged(object? sender, EventArgs e)
        {
            Send(new
            {
                type = "typing",
                from = _username,
                to = lstUsers.SelectedItem?.ToString(),
                isTyping = true
            });

            _typingTimer.Stop();
            _typingTimer.Start();
        }

        void HandleTyping(JsonElement root)
        {
            var from = root.GetProperty("from").GetString();
            var isTyping = root.GetProperty("isTyping").GetBoolean();
            if (from == null) return;

            UI(() =>
            {
                ClearTyping();
                if (isTyping)
                {
                    _typingUser = from;
                    rtbChat.AppendText($"[{from} is typing...]\n");
                }
            });
        }

        void ClearTyping()
        {
            if (_typingUser == null) return;
            var text = rtbChat.Text.Replace($"[{_typingUser} is typing...]\n", "");
            rtbChat.Text = text;
            rtbChat.SelectionStart = rtbChat.TextLength;
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

        // ===== DESIGNER SAFE =====
        private void btnGetPubKey_Click(object sender, EventArgs e) { }
        private void rtbChat_TextChanged(object sender, EventArgs e) { }
    }
}
