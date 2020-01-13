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
using Microsoft.DotNet.RemoteExecutor;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Tests
{
    public class DuplicateAndClose
    {
        private readonly ITestOutputHelper _output;
        private readonly string _ipcPipeName = Path.GetRandomFileName();
        private readonly string _semaphoreName = Path.GetRandomFileName();

        public DuplicateAndClose(ITestOutputHelper output)
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
        public void DuplicateAndClose_TcpServerHandler()
        {
            const string TestMessage = "test123!";

            using Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            listener.BindToAnonymousPort(IPAddress.Loopback);
            listener.Listen(1);

            client.Connect(listener.LocalEndPoint);
            // accept
            using Socket receiverProto = listener.Accept();

            // pipe used to exchange socket info
            using NamedPipeServerStream pipeServerStream = new NamedPipeServerStream(_ipcPipeName, PipeDirection.Out);
            using RemoteInvokeHandle hServerProc = RemoteExecutor.Invoke(HandlerServerCode, _ipcPipeName, _semaphoreName);
            pipeServerStream.WaitForConnection();

            Semaphore parentSemaphore = new Semaphore(0, 1, _semaphoreName);

            try
            {
                // Duplicate the socket:
                SocketInformation socketInfo = receiverProto.DuplicateAndClose(hServerProc.Process.Id);
                SerializationHelper.WriteSocketInfo(pipeServerStream, socketInfo);

                // Send client data:
                client.Send(Encoding.ASCII.GetBytes(TestMessage));
                bool finished = parentSemaphore.WaitOne(100);
                Assert.True(finished);
            }
            finally
            {
                hServerProc.Process.Kill();
            }

            static void HandlerServerCode(string ipcPipeName, string semaphoreName)
            {
                using NamedPipeClientStream pipeClientStream =
                    new NamedPipeClientStream(".", ipcPipeName, PipeDirection.In);
                pipeClientStream.Connect();

                SocketInformation socketInfo = SerializationHelper.ReadSocketInfo(pipeClientStream);
                using Socket handler = new Socket(socketInfo);

                Span<byte> data = stackalloc byte[128];
                int rcvCount = handler.Receive(data);
                string actual = Encoding.ASCII.GetString(data.Slice(0, rcvCount));

                Assert.Equal(TestMessage, actual);

                Semaphore.OpenExisting(semaphoreName).Release();
            }
        }
    }
}
