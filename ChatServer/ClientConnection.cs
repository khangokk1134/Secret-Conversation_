using System;
using System.IO;
using System.Net.Sockets;
using Protocol;

namespace ChatServer
{
    public class ClientConnection
    {
        private readonly TcpClient _tcp;
        private readonly Server _server;
        private readonly NetworkStream _ns;

        // serialize writes to NetworkStream (avoid packet interleaving)
        private readonly object _writeLock = new();
        private volatile bool _closed = false;

        public string? ClientId { get; private set; }
        public string? Username { get; private set; }
        public string? PublicKey { get; private set; }

        public ClientConnection(TcpClient tcp, Server server)
        {
            _tcp = tcp;
            _server = server;
            _ns = tcp.GetStream();
        }

        public void Handle()
        {
            try
            {
                while (!_closed)
                {
                    var json = PacketIO.ReadJson(_ns);   // length-prefixed
                    if (json == null) break;

                    _server.Route(json, this);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Console.WriteLine("Client error: " + ex.Message);
            }
            finally
            {
                if (!_closed && ClientId != null)
                {
                    try { _server.Unregister(ClientId); } catch { }
                }

                Close();
            }
        }

        // Server sets identity after Register
        public void SetIdentity(string clientId, string username, string pubKey)
        {
            ClientId = clientId;
            Username = username;
            PublicKey = pubKey;
        }

        public void SendRaw(string json)
        {
            if (_closed) return;
            try
            {
                lock (_writeLock)
                {
                    if (_closed) return;
                    PacketIO.SendJson(_ns, json);
                }
            }
            catch
            {
                Close();
            }
        }

        public void SendPacket<T>(T pkt)
        {
            if (_closed) return;
            try
            {
                lock (_writeLock)
                {
                    if (_closed) return;
                    PacketIO.SendPacket(_ns, pkt);
                }
            }
            catch
            {
                Close();
            }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;

            try { _ns.Close(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }
}
