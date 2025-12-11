using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace ChatServer
{
    public class Server
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private bool _running = false;

        // username -> connection
        private ConcurrentDictionary<string, ClientConnection> _clients = new ConcurrentDictionary<string, ClientConnection>();

        // offline storage folder
        private string _offlineFolder = "offline";

        public Server(int port)
        {
            if (!Directory.Exists(_offlineFolder)) Directory.CreateDirectory(_offlineFolder);

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
            Console.WriteLine("Server started.");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    var conn = new ClientConnection(tcp, this);
                    new Thread(conn.Handle).Start();
                }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine("AcceptLoop error: " + ex.Message);
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();
            foreach (var kv in _clients)
            {
                kv.Value.Close();
            }
            Console.WriteLine("Server stopped.");
        }

        public void Register(string username, ClientConnection connection, string publicKeyXml)
        {
            connection.Username = username;
            connection.PublicKeyXml = publicKeyXml;
            _clients[username] = connection;
            Console.WriteLine($"Registered user: {username}");
            BroadcastUserList();


            // deliver offline messages if any
            var file = Path.Combine(_offlineFolder, username + ".jsonl");
            if (File.Exists(file))
            {
                var lines = File.ReadAllLines(file);
                foreach (var l in lines)
                {
                    connection.SendRaw(l);
                }
                File.Delete(file);
                Console.WriteLine($"Delivered {lines.Length} offline messages to {username}");
            }
        }

        public void Unregister(string username)
        {
            _clients.TryRemove(username, out var _);
            Console.WriteLine($"Unregistered: {username}");
        }

        public string GetPublicKey(string username)
        {
            if (_clients.TryGetValue(username, out var c))
                return c.PublicKeyXml;
            return null;
        }

        // route JSON text to recipient; if offline store in offline folder
        public void RouteMessage(string toUser, string json)
        {
            if (_clients.TryGetValue(toUser, out var dest))
            {
                dest.SendRaw(json);
                Console.WriteLine($"Routed message to {toUser}");
            }
            else
            {
                // store offline
                var file = Path.Combine(_offlineFolder, toUser + ".jsonl");
                File.AppendAllText(file, json + Environment.NewLine);
                Console.WriteLine($"Stored offline message for {toUser}");
            }
        }
        public void BroadcastUserList()
        {
            var list = _clients.Keys.ToArray();

            var msg = new
            {
                type = "userlist",
                users = list
            };

            string json = JsonSerializer.Serialize(msg);

            foreach (var c in _clients.Values)
            {
                try
                {
                    c.SendRaw(json);
                }
                catch { }
            }
        }
    }
}
