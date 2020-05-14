using System;
using System.Diagnostics;
using System.Net.Sockets;

namespace ConsoleTest
{
    public class Program
    {
        public static void Main(string[] args)
        {
            using Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.Listen();
            Debugger.Break();
            Console.WriteLine("Hello World!");
        }
    }
}
