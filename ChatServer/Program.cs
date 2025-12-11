using System;

namespace ChatServer
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 5000;
            Console.WriteLine("Starting ChatServer on port " + port);
            var server = new Server(port);

            Console.WriteLine("Press ENTER to stop server...");
            Console.ReadLine();

            server.Stop();
        }
    }
}
