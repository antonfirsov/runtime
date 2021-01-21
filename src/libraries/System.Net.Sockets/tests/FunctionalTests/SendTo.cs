// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public abstract class SendToBase<T> : SocketTestHelperBase<T> where T : SocketHelperBase, new()
    {
        protected static readonly IPEndPoint ValidUdpRemoteEndpoint = new IPEndPoint(IPAddress.Parse("10.20.30.40"), 1234);

        protected SendToBase(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [InlineData(1, -1, 0)] // offset low
        [InlineData(1, 2, 0)] // offset high
        [InlineData(1, 0, -1)] // count low
        [InlineData(1, 1, 2)] // count high
        public async Task OutOfRange_Throws(int length, int offset, int count)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            ArraySegment<byte> buffer = new FakeArraySegment
            {
                Array = new byte[length], Count = count, Offset = offset
            }.ToActual();

            await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => SendToAsync(socket, buffer, ValidUdpRemoteEndpoint));
        }

        [Fact]
        public async Task NullBuffer_Throws()
        {
            if (!ValidatesArrayArguments) return;
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            await Assert.ThrowsAsync<ArgumentNullException>(() => SendToAsync(socket, null, ValidUdpRemoteEndpoint));
        }

        [Fact]
        public async Task NullEndpoint_Throws()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            await Assert.ThrowsAnyAsync<ArgumentException>(() => SendToAsync(socket, new byte[1], null));
        }

        [Fact]
        public async Task Datagram_UDP_ShouldImplicitlyBindLocalEndpoint()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            byte[] buffer = new byte[32];

            Task sendTask = SendToAsync(socket, new ArraySegment<byte>(buffer), ValidUdpRemoteEndpoint);

            // Asynchronous calls shall alter the property immediately:
            if (!UsesSync)
            {
                Assert.NotNull(socket.LocalEndPoint);
            }

            await sendTask;

            // In synchronous calls, we should wait for the completion of the helper task:
            Assert.NotNull(socket.LocalEndPoint);
        }

        [Fact]
        public async Task Datagram_UDP_AccessDenied_Throws_DoesNotBind()
        {
            IPEndPoint invalidEndpoint = new IPEndPoint(IPAddress.Broadcast, 1234);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            byte[] buffer = new byte[32];

            var e = await Assert.ThrowsAnyAsync<SocketException>(() => SendToAsync(socket, new ArraySegment<byte>(buffer), invalidEndpoint));
            Assert.Equal(SocketError.AccessDenied, e.SocketErrorCode);
            Assert.Null(socket.LocalEndPoint);
        }

        [Fact]
        public async Task Disposed_Throws()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => SendToAsync(socket, new byte[1], ValidUdpRemoteEndpoint));
        }
    }

    public sealed class SendTo_SyncSpan : SendToBase<SocketHelperSpanSync>
    {
        public SendTo_SyncSpan(ITestOutputHelper output) : base(output) { }
    }

    public sealed class SendTo_SyncSpanForceNonBlocking : SendToBase<SocketHelperSpanSyncForceNonBlocking>
    {
        public SendTo_SyncSpanForceNonBlocking(ITestOutputHelper output) : base(output) { }
    }

    public sealed class SendTo_ArraySync : SendToBase<SocketHelperArraySync>
    {
        public SendTo_ArraySync(ITestOutputHelper output) : base(output) { }
    }

    public sealed class SendTo_SyncForceNonBlocking : SendToBase<SocketHelperSyncForceNonBlocking>
    {
        public SendTo_SyncForceNonBlocking(ITestOutputHelper output) : base(output) {}
    }

    public sealed class SendTo_Apm : SendToBase<SocketHelperApm>
    {
        public SendTo_Apm(ITestOutputHelper output) : base(output) {}
    }

    public sealed class SendTo_Eap : SendToBase<SocketHelperEap>
    {
        public SendTo_Eap(ITestOutputHelper output) : base(output) {}
    }

    public sealed class SendTo_Task : SendToBase<SocketHelperTask>
    {
        public SendTo_Task(ITestOutputHelper output) : base(output) { }
    }

    public sealed class SendTo_CancellableTask : SendToBase<SocketHelperCancellableTask>
    {
        public SendTo_CancellableTask(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task PreCanceled_Throws()
        {
            using Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => sender.SendToAsync(new byte[1], SocketFlags.None, ValidUdpRemoteEndpoint, cts.Token).AsTask());

            Assert.Equal(cts.Token, ex.CancellationToken);
        }

        // On Unix, flooding the kernel with sendto calls will lead to actual asynchronous completions,
        // so we can test the cancellation logic implemented in SocketAsyncContext.
        // On Windows, WSASendTo/WSASendMsg implementations are not cancellable with CancelIoEx as of 01/2021,
        // which means CancellationToken will only take effect if it's precancelled. This may change in the future.
        [PlatformSpecific(TestPlatforms.AnyUnix)]
        [Fact]
        public async Task CancelDuringOperation_Throws()
        {
            const int DatagramCount = 100;
            const int DatagramSize = 32768;

            TimeSpan cancelAfter = TimeSpan.FromMilliseconds(1);

            await RetryHelper.ExecuteAsync(async () =>
            {
                cancelAfter /= 2;

                using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                int port = client.BindToAnonymousPort(IPAddress.Any);

                List<Task> tasks = new List<Task>();
                CancellationTokenSource cts = new CancellationTokenSource();

                // After flooding the socket with a high number of send tasks,
                // we assume some of them won't complete before the "CancelAfter" period expires.
                for (int i = 0; i < DatagramCount; i++)
                {
                    var leftTask = client.SendToAsync(new byte[DatagramSize], SocketFlags.None, ValidUdpRemoteEndpoint, cts.Token);
                    tasks.Add(leftTask.AsTask());
                }
                cts.CancelAfter(cancelAfter);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(tasks));
            }, maxAttempts: 4);
        }
    }

    public sealed class SendTo_MemoryArrayTask : SendToBase<SocketHelperMemoryArrayTask>
    {
        public SendTo_MemoryArrayTask(ITestOutputHelper output) : base(output) { }
    }

    public sealed class SendTo_MemoryNativeTask : SendToBase<SocketHelperMemoryNativeTask>
    {
        public SendTo_MemoryNativeTask(ITestOutputHelper output) : base(output) { }
    }
}
