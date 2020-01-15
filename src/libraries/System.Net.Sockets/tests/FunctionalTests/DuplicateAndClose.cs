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
    public abstract class DuplicateAndClose<T> where T : SocketHelperBase, new()
    {
        private static readonly T Helper = new T();

        private readonly byte[] _sendBuffer = new byte[32];
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[32]);

        private readonly ITestOutputHelper _output;
        private readonly string _ipcPipeName = Path.GetRandomFileName();
        private readonly string _semaphoreName = Path.GetRandomFileName();

        protected DuplicateAndClose(ITestOutputHelper output)
        {
            _output = output;
        }

        static class SerializationHelper
        {
            private static readonly BinaryFormatter BinaryFormatter = new BinaryFormatter();

            public static void WriteSocketInfo(Stream stream, SocketInformation socketInfo)
            {
                BinaryFormatter.Serialize(stream, socketInfo);
            }

            public static SocketInformation ReadSocketInfo(Stream stream)
            {
                return (SocketInformation)BinaryFormatter.Deserialize(stream);
            }
        }

        [Fact]
        public async Task DoAsyncOperation_OnBothOriginalAndClone_ThrowsInvalidOperationException()
        {
            (Socket client, Socket originalServer) =SocketTestExtensions.CreateConnectedSocketPair();

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
                Task handlerCode = Task.Run(() => HandlerServerCode(_ipcPipeName, _semaphoreName));
                RunInnerTestLogic(Process.GetCurrentProcess().Id);
                await handlerCode;
            }
            else
            {
                using RemoteInvokeHandle hServerProc = RemoteExecutor.Invoke(HandlerServerCode, _ipcPipeName, _semaphoreName);

                try
                {
                    RunInnerTestLogic(hServerProc.Process.Id);
                }
                finally
                {
                    hServerProc.Process.Kill();
                }
            }


            void RunInnerTestLogic(int processId)
            {
                pipeServerStream.WaitForConnection();
                Semaphore parentSemaphore = new Semaphore(0, 1, _semaphoreName);

                // Duplicate the socket:
                SocketInformation socketInfo = handlerOriginal.DuplicateAndClose(processId);
                SerializationHelper.WriteSocketInfo(pipeServerStream, socketInfo);

                // Send client data:
                client.Send(Encoding.ASCII.GetBytes(TestMessage));
                bool finished = parentSemaphore.WaitOne(TimeSpan.FromMilliseconds(100));
                Assert.True(finished);
            }

            static void HandlerServerCode(string ipcPipeName, string semaphoreName)
            {
                T helper = new T();
                using NamedPipeClientStream pipeClientStream =
                    new NamedPipeClientStream(".", ipcPipeName, PipeDirection.In);
                pipeClientStream.Connect();

                SocketInformation socketInfo = SerializationHelper.ReadSocketInfo(pipeClientStream);
                using Socket handler = new Socket(socketInfo);

                Assert.True(handler.IsBound);
                Assert.NotNull(handler.RemoteEndPoint);
                Assert.NotNull(handler.LocalEndPoint);

                byte[] data = new byte[128];

                int rcvCount = helper.ReceiveAsync(handler, new ArraySegment<byte>(data)).GetAwaiter().GetResult();
                string actual = Encoding.ASCII.GetString(data.AsSpan().Slice(0, rcvCount));

                Assert.Equal(TestMessage, actual);

                Semaphore.OpenExisting(semaphoreName).Release();
            }
        }
    }

    public class DuplicateAndClose
    {
        public class Apm : DuplicateAndClose<SocketHelperApm>
        {
            public Apm(ITestOutputHelper output) : base(output)
            {
            }
        }

        public class Task : DuplicateAndClose<SocketHelperTask>
        {
            public Task(ITestOutputHelper output) : base(output)
            {
            }
        }

        public class Eap : DuplicateAndClose<SocketHelperEap>
        {
            public Eap(ITestOutputHelper output) : base(output)
            {
            }
        }
    }
}
