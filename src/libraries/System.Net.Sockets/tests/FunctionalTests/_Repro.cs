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
    public class _Repro5 : _Repro { public _Repro5(ITestOutputHelper output) : base(output) { } }
    public class _Repro6 : _Repro { public _Repro6(ITestOutputHelper output) : base(output) { } }
    public class _Repro7 : _Repro { public _Repro7(ITestOutputHelper output) : base(output) { } }
    public class _Repro8 : _Repro { public _Repro8(ITestOutputHelper output) : base(output) { } }

    public abstract class _Repro
    {
        private readonly ITestOutputHelper _output;

        public _Repro(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(0)]
        // [InlineData(1)]
        // [InlineData(2)]
        // [InlineData(3)]
        // [InlineData(4)]
        // [InlineData(5)]
        // [InlineData(6)]
        // [InlineData(7)]
        // [InlineData(8)]
        // [InlineData(9)]
        // [InlineData(10)]
        public void BurnCpu(int dummy)
        {
            DoBurnCpu(30 + dummy, 4);
        }

        public const int TestInstanceCount = 30;

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

        [Theory(Timeout = 20000)]
        [MemberData(nameof(GetTestData))]
        public async Task KillUdp(bool sync, int dummy)
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

        private static BigInteger GetPi(int digits, int iterations)
        {
            return 16 * ArcTan1OverX(5, digits).ElementAt(iterations)
                - 4 * ArcTan1OverX(239, digits).ElementAt(iterations);
        }

        //arctan(x) = x - x^3/3 + x^5/5 - x^7/7 + x^9/9 - ...
        private static IEnumerable<BigInteger> ArcTan1OverX(int x, int digits)
        {
            var mag = BigInteger.Pow(10, digits);
            var sum = BigInteger.Zero;
            bool sign = true;
            for (int i = 1; true; i += 2)
            {
                var cur = mag / (BigInteger.Pow(x, i) * i);
                if (sign)
                {
                    sum += cur;
                }
                else
                {
                    sum -= cur;
                }
                yield return sum;
                sign = !sign;
            }
        }
    }
}
