using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace ChatClient
{
    public partial class RemoteClientForm : Form
    {
        private TcpClient _tcp;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _recvThread;
        private string _username;
        private string _serverIp;
        private int _serverPort;

        // keys
        private string _pubXml;
        private string _privXml;

        // cache of other users' pubkeys (username -> pubXml)
        private Dictionary<string, string> _pubCache = new Dictionary<string, string>();

        public RemoteClientForm()
        {
            InitializeComponent();
            // generate keys at start (or load if saved)
            string pub, priv;
            CryptoHelper.GenerateRsaKeys(out pub, out priv);
            _pubXml = pub;
            _privXml = priv;
            // optionally save to disk
            Directory.CreateDirectory("keys");
            File.WriteAllText(Path.Combine("keys", "my_pub.xml"), _pubXml);
            File.WriteAllText(Path.Combine("keys", "my_priv.xml"), _privXml);
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            _serverIp = txtServerIP.Text.Trim();
            _serverPort = int.Parse(txtPort.Text.Trim());
            _username = txtUser.Text.Trim();
            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(_serverIp, _serverPort);
                var ns = _tcp.GetStream();
                _reader = new StreamReader(ns, System.Text.Encoding.UTF8);
                _writer = new StreamWriter(ns, System.Text.Encoding.UTF8) { AutoFlush = true };

                // register (send my public key)
                var reg = new { type = "register", user = _username, pubkey = _pubXml };
                _writer.WriteLine(JsonSerializer.Serialize(reg));

                // start receive thread
                _recvThread = new Thread(ReceiveLoop);
                _recvThread.IsBackground = true;
                _recvThread.Start();

                rtbChat.AppendText("[Connected to server]\n");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Connect error: " + ex.Message);
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_tcp.Connected)
                {
                    string line = _reader.ReadLine();
                    if (line == null) break;

                    try
                    {
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString();

                        if (type == "pubkey")
                        {
                            var user = root.GetProperty("user").GetString();
                            var pk = root.GetProperty("pubkey").GetString();
                            if (!string.IsNullOrEmpty(pk))
                            {
                                _pubCache[user] = pk;
                                BeginInvoke(new Action(() =>
                                {
                                    if (!lstUsers.Items.Contains(user)) lstUsers.Items.Add(user);
                                    rtbChat.AppendText($"[Info] got pubkey for {user}\n");
                                }));
                            }
                        }
                        else if (type == "userlist")
                        {
                            var arr = root.GetProperty("users").EnumerateArray();

                            BeginInvoke(new Action(() =>
                            {
                                lstUsers.Items.Clear();
                                foreach (var u in arr)
                                {
                                    string name = u.GetString();
                                    if (name != _username)
                                        lstUsers.Items.Add(name);
                                }

                                rtbChat.AppendText("[User list updated]\n");
                            }));
                        }

                        else if (type == "chat")
                        {
                            var from = root.GetProperty("from").GetString();
                            var encKey = root.GetProperty("encKey").GetString();
                            var encMsg = root.GetProperty("encMsg").GetString();

                            // decrypt AES key with my private key
                            var aesKeyB64 = CryptoHelper.RsaDecryptBase64(encKey, _privXml);
                            var aesKey = Convert.FromBase64String(aesKeyB64);

                            // decrypt message
                            var plain = CryptoHelper.AesDecryptFromBase64(encMsg, aesKey);

                            BeginInvoke(new Action(() =>
                            {
                                rtbChat.AppendText($"{from}: {plain}\n");
                                if (!lstUsers.Items.Contains(from)) lstUsers.Items.Add(from);
                            }));
                        }
                    }
                    catch (Exception ex)
                    {
                        // malformed or other
                        BeginInvoke(new Action(() => rtbChat.AppendText("[ERR parse] " + ex.Message + "\n")));
                    }
                }
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() => rtbChat.AppendText("[RecvLoop error] " + ex.Message + "\n")));
            }
            finally
            {
                BeginInvoke(new Action(() => rtbChat.AppendText("[Disconnected]\n")));
            }
        }

        private void btnGetPubKey_Click(object sender, EventArgs e)
        {
            // debug: request pubkey for selected user or typed user
            string user = null;
            if (lstUsers.SelectedItem != null) user = lstUsers.SelectedItem.ToString();
            if (string.IsNullOrEmpty(user))
            {
                MessageBox.Show("Select a user in left list (or they will appear when server returns pubkey).");
                return;
            }
            var req = new { type = "get_pubkey", user = user };
            _writer.WriteLine(JsonSerializer.Serialize(req));
            rtbChat.AppendText("[Requested pubkey for " + user + "]\n");
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            if (_writer == null) { MessageBox.Show("Not connected"); return; }

            string to;
            if (lstUsers.SelectedItem == null)
            {
                MessageBox.Show("Select recipient from left list.");
                return;
            }
            to = lstUsers.SelectedItem.ToString();
            if (to == _username) { MessageBox.Show("Cannot send to yourself"); return; }

            // ensure we have public key for recipient
            if (!_pubCache.ContainsKey(to))
            {
                // request from server synchronously: send request, wait a moment for reply (simple)
                var req = new { type = "get_pubkey", user = to };
                _writer.WriteLine(JsonSerializer.Serialize(req));
                rtbChat.AppendText("[Requested pubkey, wait a moment]\n");
                // naive wait up to 1s for reply (for demo). In real app, implement async callback.
                int waited = 0;
                while (!_pubCache.ContainsKey(to) && waited < 1000)
                {
                    System.Threading.Thread.Sleep(50);
                    waited += 50;
                }
                if (!_pubCache.ContainsKey(to))
                {
                    MessageBox.Show("No pubkey received for " + to);
                    return;
                }
            }

            var recipientPub = _pubCache[to];

            var plain = txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(plain)) return;

            // 1) generate AES key
            var aesKey = CryptoHelper.GenerateAesKey(); // bytes
            var aesKeyB64 = Convert.ToBase64String(aesKey);

            // 2) encrypt message with AES
            var encMsg = CryptoHelper.AesEncryptToBase64(plain, aesKey);

            // 3) encrypt AES key using recipient's public RSA key
            var encKey = CryptoHelper.RsaEncryptBase64(aesKeyB64, recipientPub);

            var obj = new
            {
                type = "chat",
                from = _username,
                to = to,
                encKey = encKey,
                encMsg = encMsg,
                time = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(obj);
            _writer.WriteLine(json);

            rtbChat.AppendText($"Me -> {to}: {plain}\n");
            txtMessage.Clear();
        }

        private void rtbChat_TextChanged(object sender, EventArgs e)
        {

        }
    }
}
