// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Runtime.InteropServices;

namespace System.Net.Sockets.Tests
{
    internal static class SocketTestUtils
    {
        public static string GetRandomNonExistingFilePath()
        {
            string result;
            do
            {
                result = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            }
            while (File.Exists(result));

            return result;
        }

        public static bool PlatformSupportsUnixDomainSockets
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Tcp);
                    }
                    catch (SocketException se)
                    {
                        return se.SocketErrorCode != SocketError.AddressFamilyNotSupported;
                    }
                }

                return true;
            }
        }


    }
}
