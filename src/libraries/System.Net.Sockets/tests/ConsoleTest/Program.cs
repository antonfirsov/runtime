using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace ConsoleTest
{
    class Bela
    {
        public Bela(int value)
        {
            Value = value;
        }

        public int Value { get; }

        public void Hello(int x)
        {
            int y = x + Value;
            Bela bb = new Bela(y);
            Console.WriteLine($"bb = {bb}");
            Debugger.Break();
        }

        public static void DoBealStuff()
        {
            for (int i = 0; i < 5; i++)
            {
                Bela b = new Bela(i);
                int x = b.Value * b.Value;
                Console.WriteLine($"i == {i} | x == {x}");
                b.Hello(x);
            }
        }

    }

    class A
    {
        public static int TotalCount;

        public A()
        {
            Interlocked.Increment(ref TotalCount);
        }

        ~A()
        {
            Interlocked.Decrement(ref TotalCount);
        }
    }

    public class Program
    {
        public static Task Main()
        {
            return SocketDisposeTestAsync();
            //return DumbTest();
        }

        private static async Task DumbTest()
        {
            List<WeakReference> refList = await CreateTestReferences();

            Console.WriteLine($"Total Count before: {A.TotalCount}");

            GC.Collect();
            GC.WaitForPendingFinalizers();

            Console.WriteLine($"Total Count after: {A.TotalCount}");

            int alive = refList.Count(h => h.IsAlive);
            Console.WriteLine($"Handles alive: {alive}");

            Debugger.Break();
        }

        private static async Task<List<WeakReference>> CreateTestReferences()
        {
            await Task.CompletedTask;

            List<A> list = new List<A>();
            for (int i = 0; i < 100; i++)
            {
                list.Add(new A());
            }

            return list.Select(a => new WeakReference(a)).ToList();
        }

        private static async Task SocketDisposeTestAsync()
        {
            List<WeakReference> refList = await CreateHandlesAsync(true);

            Console.WriteLine($"Total Count before: {SafeSocketHandle.TotalCount}");

            await Task.Delay(1);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Console.WriteLine($"Total Count after: {SafeSocketHandle.TotalCount}");

            int alive = refList.Count(h => h.IsAlive);
            Console.WriteLine($"Handles alive: {alive}");

            var r = refList.First(h => h.IsAlive);
            SafeSocketHandle handle = (SafeSocketHandle)r.Target;
            Console.WriteLine($"H: {handle} TrackResurrection: {r.TrackResurrection}");
            Debugger.Break();
        }

        private static void SocketDisposeTest()
        {
            List<WeakReference> refList = CreateHandles();

            Console.WriteLine($"Total Count before: {SafeSocketHandle.TotalCount}");

            GC.Collect();
            GC.WaitForPendingFinalizers();

            Console.WriteLine($"Total Count after: {SafeSocketHandle.TotalCount}");

            int alive = refList.Count(h => h.IsAlive);
            Console.WriteLine($"Handles alive: {alive}");

            SafeSocketHandle handle = (SafeSocketHandle)refList.First(h => h.IsAlive).Target;
            Console.WriteLine($"H: {handle}");
            Debugger.Break();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<WeakReference> CreateHandles() => CreateHandlesAsync(true).GetAwaiter().GetResult();


        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<List<WeakReference>> CreateHandlesAsync(bool clientAsync)
        {
            var handles = new List<WeakReference>();

            using (var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen();

                for (int i = 0; i < 100; i++)
                {
                    var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); // do not dispose
                    handles.Add(new WeakReference(client.SafeHandle));
                    if (clientAsync)
                    {
                        await client.ConnectAsync(listener.LocalEndPoint);
                    }
                    else
                    {
                        client.Connect(listener.LocalEndPoint);
                    }

                    using (Socket server = listener.Accept())
                    {
                        if (clientAsync)
                        {
                            Task<int> receiveTask = client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), SocketFlags.None);
                            server.Send(new byte[1]);
                            await receiveTask;
                        }
                        else
                        {
                            server.Send(new byte[1]);
                            client.Receive(new byte[1]);
                        }
                    }
                }
            }

            return handles;
        }
    }
}
