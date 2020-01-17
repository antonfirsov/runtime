// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    // Test cases for DuplicateAndClose, and related API-s.
    public abstract class SocketDuplicationTests<T> where T : SocketHelperBase, new()
    {
        private static readonly T Helper = new T();

        private readonly byte[] _sendBuffer = new byte[32];
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[32]);

        private readonly ITestOutputHelper _output;
        private readonly string _ipcPipeName = Path.GetRandomFileName();

        public static bool IsAsync => !Helper.UsesSync;

        private static int CurrentProcessId => Process.GetCurrentProcess().Id;

        const string TestMessage = "test123!";
        private static byte[] TestBytes => Encoding.ASCII.GetBytes(TestMessage);

        private static string GetMessageString(byte[] data, int count) =>
            Encoding.ASCII.GetString(data.AsSpan().Slice(0, count));

        protected SocketDuplicationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        static class SerializationHelper
        {
            public static void WriteSocketInfo(Stream stream, SocketInformation socketInfo)
            {
                BinaryWriter bw = new BinaryWriter(stream);
                bw.Write((int)socketInfo.Options);
                bw.Write(socketInfo.ProtocolInformation.Length);
                bw.Write(socketInfo.ProtocolInformation);
            }

            public static SocketInformation ReadSocketInfo(Stream stream)
            {
                BinaryReader br = new BinaryReader(stream);
                SocketInformationOptions options = (SocketInformationOptions)br.ReadInt32();
                int protocolInfoLength = br.ReadInt32();
                SocketInformation result = new SocketInformation()
                {
                    Options = options,
                    ProtocolInformation = new byte[protocolInfoLength]
                };
                br.Read(result.ProtocolInformation);
                return result;
            }
        }

        [Fact]
        [PlatformSpecific(TestPlatforms.Windows)]
        public async Task DoAsyncOperation_OnBothOriginalAndClone_ThrowsInvalidOperationException()
        {
            if (!IsAsync) return;

            // Not applicable for synchronous operations:
            (Socket client, Socket originalServer) = SocketTestExtensions.CreateConnectedSocketPair();

            using (client)
            using (originalServer)
            {
                client.Send(_sendBuffer);

                await Helper.ReceiveAsync(originalServer, _receiveBuffer);

                SocketInformation info = originalServer.DuplicateAndClose(CurrentProcessId);

                using Socket cloneServer = new Socket(info);
                await Assert.ThrowsAsync<InvalidOperationException>( () => Helper.ReceiveAsync(cloneServer, _receiveBuffer));
            }
        }

        [Theory]
        [PlatformSpecific(TestPlatforms.Windows)]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DuplicateAndClose_TcpServerHandler(bool sameProcess)
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            listener.BindToAnonymousPort(IPAddress.Loopback);
            listener.Listen(1);

            client.Connect(listener.LocalEndPoint);

            // Async is allowed on the listener:
            using Socket handlerOriginal = await listener.AcceptAsync();

            // pipe used to exchange socket info
            await using NamedPipeServerStream pipeServerStream = new NamedPipeServerStream(_ipcPipeName, PipeDirection.Out);

            if (sameProcess)
            {
                Task handlerCode = Task.Run(() => HandlerServerCode(_ipcPipeName));
                RunCommonHostLogic(CurrentProcessId);
                await handlerCode;
            }
            else
            {
                RemoteInvokeOptions options = new RemoteInvokeOptions() {TimeOut = 500};
                using RemoteInvokeHandle hServerProc = RemoteExecutor.Invoke(HandlerServerCode, _ipcPipeName, options);
                RunCommonHostLogic(hServerProc.Process.Id);
            }

            void RunCommonHostLogic(int processId)
            {
                pipeServerStream.WaitForConnection();

                // Duplicate the socket:
                SocketInformation socketInfo = handlerOriginal.DuplicateAndClose(processId);
                SerializationHelper.WriteSocketInfo(pipeServerStream, socketInfo);

                // Send client data:
                client.Send(TestBytes);
            }

            static async Task<int> HandlerServerCode(string ipcPipeName)
            {
                await using NamedPipeClientStream pipeClientStream =
                    new NamedPipeClientStream(".", ipcPipeName, PipeDirection.In);
                pipeClientStream.Connect();

                SocketInformation socketInfo = SerializationHelper.ReadSocketInfo(pipeClientStream);
                using Socket handler = new Socket(socketInfo);

                Assert.True(handler.IsBound);
                Assert.NotNull(handler.RemoteEndPoint);
                Assert.NotNull(handler.LocalEndPoint);

                byte[] data = new byte[32];

                int rcvCount = await Helper.ReceiveAsync(handler, new ArraySegment<byte>(data));
                string actual = GetMessageString(data, rcvCount);

                Assert.Equal(TestMessage, actual);

                return RemoteExecutor.SuccessExitCode;
            }
        }

        [PlatformSpecific(TestPlatforms.Windows)]
        [Fact]
        public async Task DuplicateAndClose_TcpClient()
        {
            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using Socket client0 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            using Socket client1 = new Socket(client0.DuplicateAndClose(CurrentProcessId));
            Assert.False(client1.Connected);
            client1.Connect(listener.LocalEndPoint);

            using Socket client2 = new Socket(client1.DuplicateAndClose(CurrentProcessId));
            Assert.True(client2.Connected);

            using Socket handler = await Helper.AcceptAsync(listener);
            await Helper.SendAsync(client2, TestBytes);

            byte[] receivedBuffer = new byte[32];
            int rcvCount = await Helper.ReceiveAsync(handler, new ArraySegment<byte>(receivedBuffer));

            string receivedMessage = GetMessageString(receivedBuffer, rcvCount);
            Assert.Equal(TestMessage, receivedMessage);
        }

        [PlatformSpecific(TestPlatforms.Windows)]
        [Fact]
        public async Task DuplicateAndClose_TcpListener()
        {
            using Socket listener0 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener0.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener0.Listen(1);

            using Socket listener1 = new Socket(listener0.DuplicateAndClose(CurrentProcessId));

            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _ = client.ConnectAsync(listener1.LocalEndPoint);

            using Socket handler = await Helper.AcceptAsync(listener1);
            await Helper.SendAsync(client, TestBytes);

            byte[] receivedBuffer = new byte[32];
            int rcvCount = await Helper.ReceiveAsync(handler, new ArraySegment<byte>(receivedBuffer));

            string receivedMessage = GetMessageString(receivedBuffer, rcvCount);
            Assert.Equal(TestMessage, receivedMessage);
        }
    }

    public class SocketDuplicationTests
    {
        private static int CurrentProcessId => Process.GetCurrentProcess().Id;

        [Fact]
        public void UseOnlyOverlappedIO_AlwaysFalse()
        {
            using Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            Assert.False(s.UseOnlyOverlappedIO);
            s.UseOnlyOverlappedIO = true;
            Assert.False(s.UseOnlyOverlappedIO);
        }

        [PlatformSpecific(TestPlatforms.Windows)]
        [Fact]
        public void DuplicateAndClose_TargetProcessDoesNotExist_Throws_SocketException()
        {
            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            Assert.Throws<SocketException>(() => socket.DuplicateAndClose(-1));
        }

        [PlatformSpecific(TestPlatforms.Windows)]
        [Fact]
        public void DuplicateAndClose_WhenDisposed_Throws()
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Dispose();

            Assert.Throws<ObjectDisposedException>(() => socket.DuplicateAndClose(CurrentProcessId));
        }

        [PlatformSpecific(TestPlatforms.Windows)]
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BlockingState_IsTransferred(bool blocking)
        {
            using Socket original = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                Blocking = blocking
            };
            Assert.Equal(blocking, original.Blocking);

            SocketInformation info = original.DuplicateAndClose(CurrentProcessId);

            using Socket clone = new Socket(info);
            Assert.Equal(blocking, clone.Blocking);
        }

        public class NotSupportedOnUnix
        {
            [PlatformSpecific(TestPlatforms.AnyUnix)]
            [Fact]
            public void SocketCtr_SocketInformation()
            {
                SocketInformation socketInformation = default;
                Assert.Throws<PlatformNotSupportedException>(() => new Socket(socketInformation));
            }

            [PlatformSpecific(TestPlatforms.AnyUnix)]
            [Fact]
            public void DuplicateAndClose()
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                int processId = CurrentProcessId;

                Assert.Throws<PlatformNotSupportedException>(() => socket.DuplicateAndClose(processId));
            }
        }

        public class Synchronous : SocketDuplicationTests<SocketHelperArraySync>
        {
            public Synchronous(ITestOutputHelper output) : base(output)
            {
            }
        }

        public class Apm : SocketDuplicationTests<SocketHelperApm>
        {
            public Apm(ITestOutputHelper output) : base(output)
            {
            }
        }

        public class TaskBased : SocketDuplicationTests<SocketHelperTask>
        {
            public TaskBased(ITestOutputHelper output) : base(output)
            {
            }
        }

        public class Eap : SocketDuplicationTests<SocketHelperEap>
        {
            public Eap(ITestOutputHelper output) : base(output)
            {
            }
        }
    }
}
