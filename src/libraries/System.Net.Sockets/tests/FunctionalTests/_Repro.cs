// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public class _Repro1 : _Repro { public _Repro1(ITestOutputHelper output) : base(output) { } }
    public class _Repro2 : _Repro { public _Repro2(ITestOutputHelper output) : base(output) { } }
    public class _Repro3 : _Repro { public _Repro3(ITestOutputHelper output) : base(output) { } }
    public class _Repro4 : _Repro { public _Repro4(ITestOutputHelper output) : base(output) { } }
    // public class _Repro5 : _Repro { public _Repro5(ITestOutputHelper output) : base(output) { } }
    // public class _Repro6 : _Repro { public _Repro6(ITestOutputHelper output) : base(output) { } }
    // public class _Repro7 : _Repro { public _Repro7(ITestOutputHelper output) : base(output) { } }
    // public class _Repro8 : _Repro { public _Repro8(ITestOutputHelper output) : base(output) { } }

    public abstract class _Repro
    {
        private readonly ITestOutputHelper _output;

        public _Repro(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void BurnCpu1() => DoBurnCpu(15, 4);

        // [Fact]
        // public void BurnCpu2() => DoBurnCpu(30, 4);

        // [Fact]
        // public void BurnCpu3() => DoBurnCpu(30, 4);

        // [Fact]
        // public void BurnCpu4() => DoBurnCpu(30, 4);

        // [Fact]
        // public void BurnCpu5() => DoBurnCpu(30, 4);

        // [Fact]
        // public void BurnCpu6() => DoBurnCpu(30, 4);

        // [Fact]
        // public void BurnCpu7() => DoBurnCpu(30, 4);

        // [Fact]
        // public void BurnCpu8() => DoBurnCpu(30, 4);

        public const int TestInstanceCount = 5;

        public static TheoryData<bool, int> GetTestData()
        {
            TheoryData<bool, int> result = new TheoryData<bool, int>();
            for (int i = 0; i < TestInstanceCount; i++)
            {
                result.Add(true, i);
                result.Add(false, i);
            }
            return result;
        }

        private static SocketHelperBase CreateHelper(bool sync) => sync ?
            new SocketHelperArraySync() :
            new SocketHelperMemoryArrayTask();

        //[Theory(Timeout = 20000)]
        //[MemberData(nameof(GetTestData))]
        //public Task KillUdp(bool sync, int dummy) => KillUdpImpl(sync, dummy);

        [Fact(Timeout = 15000)]
        public Task KillUdp_Async() => KillUdpImpl(false, 0);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_Sync() => KillUdpImpl(true, 1);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_03() => KillUdpImpl(false, 2);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_04() => KillUdpImpl(false, 3);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_05() => KillUdpImpl(false, 0);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_06() => KillUdpImpl(false, 1);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_07() => KillUdpImpl(false, 2);

        // [Fact(Timeout = 20000)]
        // public Task KillUdp_08() => KillUdpImpl(false, 3);

        private async Task KillUdpImpl(bool sync, int dummy)
        {
            IPAddress leftAddress = IPAddress.Loopback, rightAddress = IPAddress.Loopback;

            int DatagramSize = 256 + dummy / 10000;
            const int DatagramsToSend = 256;
            const int AckTimeout = 2000;
            const int TestTimeout = 5000;
            var helper = CreateHelper(sync);

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
                    SocketReceiveFromResult result = await helper.ReceiveFromAsync(
                        left, new ArraySegment<byte>(recvBuffer), remote);
                    Assert.Equal(DatagramSize, result.ReceivedBytes);
                    Assert.Equal(rightEndpoint, result.RemoteEndPoint);

                    int datagramId = recvBuffer[0];
                    Assert.Null(receivedChecksums[datagramId]);
                    receivedChecksums[datagramId] = Fletcher32.Checksum(recvBuffer, 0, result.ReceivedBytes);

                    receiverAck.Release();
                    _output.WriteLine($"senderAck.WaitAsync [{i}] ...");
                    bool gotAck = await senderAck.WaitAsync(TestTimeout);
                    _output.WriteLine($"senderAck.WaitAsync [{i}] returned");
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

                    int sent = await helper.SendToAsync(right, new ArraySegment<byte>(sendBuffer), leftEndpoint);

                    _output.WriteLine($"receiverAck.WaitAsync [{i}] ...");
                    bool gotAck = await receiverAck.WaitAsync(AckTimeout);
                    _output.WriteLine($"receiverAck.WaitAsync [{i}] returned");

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
            List<Task> tasks = new List<Task>();

            for (int i = 0; i < parallelism; i++)
            {
                tasks.Add(Task.Factory.StartNew(BurnPlease, TaskCreationOptions.LongRunning));
            }

            Task.WhenAll(tasks).GetAwaiter().GetResult();

            void BurnPlease()
            {
                TimeSpan dt = TimeSpan.FromSeconds(seconds);
                DateTime end = DateTime.Now + dt;

                while (DateTime.Now < end)
                {
                    FindPrimeNumber(500);
                }
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
