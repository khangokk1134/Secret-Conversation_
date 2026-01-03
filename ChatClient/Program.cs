using System;
using System.Windows.Forms;

namespace ChatClient
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ChatAppForm());
        }
    }
}
