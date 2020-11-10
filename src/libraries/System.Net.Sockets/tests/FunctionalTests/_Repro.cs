// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public class _Repro
    {
        private readonly ITestOutputHelper _output;
        private static SocketHelperArraySync _helper = new SocketHelperArraySync();

        public _Repro(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void BurnCpu()
        {
            DoBurnCpu(60, 4);
        }

        [Fact]
        public async Task SendToRecvFrom_Datagram_UDP()
        {
            IPAddress leftAddress = IPAddress.Loopback, rightAddress = IPAddress.Loopback;

            const int DatagramSize = 256;
            const int DatagramsToSend = 256;
            const int AckTimeout = 2000;
            const int TestTimeout = 5000;

            using var origLeft = new Socket(leftAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using var origRight = new Socket(rightAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            origLeft.BindToAnonymousPort(leftAddress);
            origRight.BindToAnonymousPort(rightAddress);

            using var left = false ? new Socket(origLeft.SafeHandle) : origLeft;
            using var right = false ? new Socket(origRight.SafeHandle) : origRight;

            var leftEndpoint = (IPEndPoint)left.LocalEndPoint;
            var rightEndpoint = (IPEndPoint)right.LocalEndPoint;

            var receiverAck = new SemaphoreSlim(0);
            var senderAck = new SemaphoreSlim(0);

            _output.WriteLine($"{DateTime.Now}: Sending data from {rightEndpoint} to {leftEndpoint}");

            var receivedChecksums = new uint?[DatagramsToSend];
            Task leftThread = Task.Run(async () =>
            {
                EndPoint remote = leftEndpoint.Create(leftEndpoint.Serialize());
                var recvBuffer = new byte[DatagramSize];
                for (int i = 0; i < DatagramsToSend; i++)
                {
                    SocketReceiveFromResult result = await _helper.ReceiveFromAsync(
                        left, new ArraySegment<byte>(recvBuffer), remote);
                    Assert.Equal(DatagramSize, result.ReceivedBytes);
                    Assert.Equal(rightEndpoint, result.RemoteEndPoint);

                    int datagramId = recvBuffer[0];
                    Assert.Null(receivedChecksums[datagramId]);
                    receivedChecksums[datagramId] = Fletcher32.Checksum(recvBuffer, 0, result.ReceivedBytes);

                    receiverAck.Release();
                    bool gotAck = await senderAck.WaitAsync(TestTimeout);
                    Assert.True(gotAck, $"{DateTime.Now}: Timeout waiting {TestTimeout} for senderAck in iteration {i}");
                }
            });

            var sentChecksums = new uint[DatagramsToSend];
            using (right)
            {
                var random = new Random();
                var sendBuffer = new byte[DatagramSize];
                for (int i = 0; i < DatagramsToSend; i++)
                {
                    random.NextBytes(sendBuffer);
                    sendBuffer[0] = (byte)i;

                    int sent = await _helper.SendToAsync(right, new ArraySegment<byte>(sendBuffer), leftEndpoint);

                    bool gotAck = await receiverAck.WaitAsync(AckTimeout);

                    if (!gotAck)
                    {
                        string msg = $"{DateTime.Now}: Timeout waiting {AckTimeout} for receiverAck in iteration {i} after sending {sent}. Receiver is in {leftThread.Status}";
                        if (leftThread.Status == TaskStatus.Faulted)
                        {
                            try
                            {
                                await leftThread;
                            }
                            catch (Exception ex)
                            {
                                msg += $"{ex.Message}{Environment.NewLine}{ex.StackTrace}";
                            }
                        }
                        Assert.False(true, msg);
                    }

                    senderAck.Release();

                    Assert.Equal(DatagramSize, sent);
                    sentChecksums[i] = Fletcher32.Checksum(sendBuffer, 0, sent);
                }
            }

            await leftThread;
            for (int i = 0; i < DatagramsToSend; i++)
            {
                Assert.NotNull(receivedChecksums[i]);
                Assert.Equal(sentChecksums[i], (uint)receivedChecksums[i]);
            }
        }

        private static void DoBurnCpu(int seconds, int parallelism)
        {
            TimeSpan dt = TimeSpan.FromSeconds(seconds);
            DateTime end = DateTime.Now + dt;

            while (DateTime.Now < end)
            {
                Parallel.For(0, parallelism, i => _ = FindPrimeNumber(500 + i));
            }
        }

        private static long FindPrimeNumber(int n)
        {
            int count = 0;
            long a = 2;
            while (count < n)
            {
                long b = 2;
                int prime = 1;// to check if found a prime
                while (b * b <= a)
                {
                    if (a % b == 0)
                    {
                        prime = 0;
                        break;
                    }
                    b++;
                }
                if (prime > 0)
                {
                    count++;
                }
                a++;
            }
            return (--a);
        }
    }
}
