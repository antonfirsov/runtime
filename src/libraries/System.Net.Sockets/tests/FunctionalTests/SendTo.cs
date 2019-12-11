// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public class SendTo : IDisposable
    {
        private readonly Socket _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly IPEndPoint _remoteEndPoint = new IPEndPoint(IPAddress.Parse("10.20.30.40"), 1234);

        private readonly ITestOutputHelper _output;

        private byte[] _buffer = new byte[64];

        public SendTo(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Udp_SendTo_ShouldBindLocalEndpoint()
        {
            _socket.SendTo(_buffer, _remoteEndPoint);

            Assert.NotNull(_socket.LocalEndPoint);
        }

        [Fact]
        public void Udp_BeginSendTo_ShouldBindLocalEndpoint()
        {
            var iar = _socket.BeginSendTo(_buffer, 0, _buffer.Length, SocketFlags.None, _remoteEndPoint, _ => { }, null);
            Assert.NotNull(_socket.LocalEndPoint);

            _socket.EndSendTo(iar);
        }

        [Fact]
        public async Task Udp_SendToAsync_Task_ShouldBindLocalEndpoint()
        {
            Task task = _socket.SendToAsync(_buffer, SocketFlags.None, _remoteEndPoint);
            Assert.NotNull(_socket.LocalEndPoint);
            await task;
        }

        [Fact]
        public void Udp_SendToAsync_EventArgs_ShouldBindLocalEndpoint()
        {
            var socketAsyncEventArgs = new SocketAsyncEventArgs
            {
                RemoteEndPoint = _remoteEndPoint
            };
            
            socketAsyncEventArgs.SetBuffer(_buffer, 0, 32);
            socketAsyncEventArgs.Completed += (_, e) => SendCompleted(e);

            if (!_socket.SendToAsync(socketAsyncEventArgs))
            {
                SendCompleted(socketAsyncEventArgs);
            }

            Assert.NotNull(_socket.LocalEndPoint);

            static void SendCompleted(SocketAsyncEventArgs args)
            {
                args.Dispose();
            }
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}
