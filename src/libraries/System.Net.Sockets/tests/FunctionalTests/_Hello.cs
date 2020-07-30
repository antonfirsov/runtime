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

        [Fact(Skip ="kussolj")]
        public async Task BasicSendReceive()
        {
            byte[] sendBuffer = new byte[32];
            byte[] receiveBuffer = new byte[32];

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

        [Fact]
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
