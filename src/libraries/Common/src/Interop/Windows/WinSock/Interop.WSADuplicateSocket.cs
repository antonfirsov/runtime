using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Winsock
    {
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)]
        internal struct WSAProtocolChain
        {
            internal int ChainLen;                                 /* the length of the chain,     */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=7)]
            internal uint[] ChainEntries;       /* a list of dwCatalogEntryIds */
        }

        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)]
        internal struct WSAProtocolInfo
        {
            internal uint dwServiceFlags1;
            internal uint dwServiceFlags2;
            internal uint dwServiceFlags3;
            internal uint dwServiceFlags4;
            internal uint dwProviderFlags;
            internal Guid ProviderId;
            internal uint dwCatalogEntryId;
            internal WSAProtocolChain ProtocolChain;
            internal int iVersion;
            internal AddressFamily iAddressFamily;
            internal int iMaxSockAddr;
            internal int iMinSockAddr;
            internal SocketType iSocketType;
            internal ProtocolType iProtocol;
            internal int iProtocolMaxOffset;
            internal int iNetworkByteOrder;
            internal int iSecurityScheme;
            internal uint dwMessageSize;
            internal uint dwProviderReserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)]
            internal string szProtocol;

            public static readonly int _Size = Marshal.SizeOf(typeof(WSAProtocolInfo));
        }

        [DllImport(Interop.Libraries.Ws2_32, SetLastError = true)]
        internal static extern unsafe SocketError WSADuplicateSocket(
            [In] SafeHandle socketHandle,
            [In] uint targetProcessID,
            [In] byte* pinnedBuffer
        );
    }
}
