// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.Common.ExtensionFramework.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public class _Hello : SocketTestHelperBase<SocketHelperEap>
    {
        public _Hello(ITestOutputHelper output) : base(output)
        {
        }

        public static TheoryData<int> GetDisposeAfterData()
        {
            TheoryData<int> result = new TheoryData<int>();

            for (int i = 0; i < 10; i++)
            {
                result.Add(8000 + 2 * i);
            }

            return result;
        }

        [Theory(Timeout = 5000)]
        [MemberData(nameof(GetDisposeAfterData))]
        public async Task ReproMaybe(int disposeAfterReceives)
        {
            byte[] sendBuffer = new byte[512];
            byte[] receiveBuffer = new byte[512];

            int msgCount = 10000;

            (Socket client, Socket server) = SocketTestExtensions.CreateConnectedSocketPair();

            List<Task> allTasks = new List<Task>();
            List<Task> receiveTasks = new List<Task>();

            ManualResetEventSlim shouldDisposeNow = new ManualResetEventSlim();

            Task disposeTask = Task.Factory.StartNew(() =>
            {
                shouldDisposeNow.Wait();
                client.Shutdown(SocketShutdown.Both);
                client.Close(15);
            }, TaskCreationOptions.LongRunning);

            for (int i = 0; i < msgCount; i++)
            {
                Task sendTask = SendAsync(server, sendBuffer);
                allTasks.Add(sendTask);
            }

            for (int i = 0; i < msgCount; i++)
            {
                if (client.SafeHandle.IsClosed) continue;

                Task receiveTask = ReceiveAsync(client, receiveBuffer);

                if (i == disposeAfterReceives)
                {
                    if (receiveTask.IsCompleted)
                    {
                        Console.WriteLine($"Firing the dispose @ {i} [sync]");
                        shouldDisposeNow.Set();
                    }
                    else
                    {
                        receiveTask.GetAwaiter().OnCompleted(() =>
                        {
                            Console.WriteLine($"Firing dispose @ {i} [async]");
                            shouldDisposeNow.Set();
                        });
                    }
                }

                allTasks.Add(receiveTask);
                receiveTasks.Add(receiveTask);
            }

            try
            {
                await Task.WhenAll(receiveTasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Got exception: {ex.GetType()} | {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            
            client.Dispose();
            server.Dispose();
        }

        [Fact(Skip = "kussolj")]
        public async Task BasicSendReceive1()
        {
            byte[] sendBuffer = new byte[512];
            byte[] receiveBuffer = new byte[512];

            int msgCount = 10000;

            (Socket client, Socket server) = SocketTestExtensions.CreateConnectedSocketPair();

            List<Task> allTasks = new List<Task>();

            for (int i = 0; i < msgCount; i++)
            {
                Task sendTask = SendAsync(server, sendBuffer);
                allTasks.Add(sendTask);
            }

            for (int i = 0; i < msgCount; i++)
            {
                Task receiveTask = ReceiveAsync(client, receiveBuffer);
                allTasks.Add(receiveTask);
            }

            await Task.WhenAll(allTasks);
            
            client.Dispose();
            server.Dispose();
        }

        [Fact(Skip = "kussolj")]
        public async Task BasicSendReceive2()
        {
            byte[] sendBuffer = new byte[32];
            byte[] receiveBuffer = new byte[32];

            int msgCount = 10000;

            (Socket client, Socket server) = SocketTestExtensions.CreateConnectedSocketPair();

            List<Task> allTasks = new List<Task>();

            for (int i = 0; i < msgCount; i++)
            {
                Task sendTask = SendAsync(server, sendBuffer);
                //allTasks.Add(sendTask);
                await sendTask;
            }

            for (int i = 0; i < msgCount; i++)
            {
                Task receiveTask = ReceiveAsync(client, receiveBuffer);
                allTasks.Add(receiveTask);
            }

            await Task.WhenAll(allTasks);

            client.Dispose();
            server.Dispose();
        }

        //[Fact]
        //public void SuchSuccess()
        //{
        //    Assert.True(true);
        //}

        //[Fact]
        //public void SuchFailure()
        //{
        //    Assert.True(false);
        //}
    }
}
