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

                SocketInformation info = originalServer.DuplicateAndClose(Process.GetCurrentProcess().Id);

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
            const string TestMessage = "test123!";

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
                RunCommonHostLogic(Process.GetCurrentProcess().Id);
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
                client.Send(Encoding.ASCII.GetBytes(TestMessage));
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

                byte[] data = new byte[128];

                int rcvCount = await Helper.ReceiveAsync(handler, new ArraySegment<byte>(data));
                string actual = Encoding.ASCII.GetString(data.AsSpan().Slice(0, rcvCount));

                Assert.Equal(TestMessage, actual);

                return RemoteExecutor.SuccessExitCode;
            }
        }
    }

    public class SocketDuplicationTests
    {
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

            Assert.Throws<ObjectDisposedException>(() => socket.DuplicateAndClose(Process.GetCurrentProcess().Id));
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
                int processId = Process.GetCurrentProcess().Id;

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
