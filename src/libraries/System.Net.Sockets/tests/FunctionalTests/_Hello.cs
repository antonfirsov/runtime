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
    public class _Hello 
    {
        [Fact]
        public async Task BasicSendReceive()
        {
            byte[] sendBuffer = new byte[256];
            byte[] receiveBuffer = new byte[256];

            int msgCount = 20;

            (Socket client, Socket server) = SocketTestExtensions.CreateConnectedSocketPair();

            List<Task> allTasks = new List<Task>();

            for (int i = 0; i < msgCount; i++)
            {
                Task sendTask = client.SendAsync(sendBuffer, SocketFlags.None);
                allTasks.Add(sendTask);
            }

            for (int i = 0; i < msgCount; i++)
            {
                Task receiveTask = server.ReceiveAsync(receiveBuffer, SocketFlags.None);
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
