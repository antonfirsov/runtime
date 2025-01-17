// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Net.Sockets
{
    public partial class SafeSocketHandle
    {
        private int _trackedOptions;

        public void TrackSocketOption(SocketOptionLevel level, SocketOptionName name)
        {
            TrackedSocketOptions tracked = ToTrackedSocketOptions(name, level);
            _trackedOptions |= 1 << ((int)tracked - 1);

            if (tracked == TrackedSocketOptions.None)
            {
                ExposedHandleOrUntrackedConfiguration = true;
            }
        }

        public int GetTrackedOptions(Span<int> values, out LingerOption? lingerOption)
        {
            Debug.Assert(values.Length == LastTrackedSocketOptionIndex + 1);

            // SO_LINGER is the only tracked socket option with a non-int value.
            lingerOption = null;
            int lingerMask = 1 << (int)TrackedSocketOptions.SO_LINGER;
            int trackedOptions = _trackedOptions;
            if ((trackedOptions & lingerMask) == lingerMask)
            {
                SocketError errorCode = SocketPal.GetLingerOption(this, out lingerOption);
                if (NetEventSource.Log.IsEnabled() && errorCode != SocketError.Success) NetEventSource.Info(this, $"GetLingerOption returned errorCode:{errorCode}");
                trackedOptions &= ~lingerMask;
            }

            for (int i = 0; i < values.Length; i++)
            {
                int mask = 1 << i;
                if ((trackedOptions & mask) == mask)
                {
                    TrackedSocketOptions tracked = (TrackedSocketOptions)(i + 1);
                    (SocketOptionName name, SocketOptionLevel level) = ToSocketOptions(tracked);
                    SocketError errorCode = SocketPal.GetSockOpt(this, level, name, out values[i]);
                    if (NetEventSource.Log.IsEnabled() && errorCode != SocketError.Success) NetEventSource.Info(this, $"GetSockOpt({level},{name}) returned errorCode:{errorCode}");
                }
            }
            return trackedOptions;
        }

        public void SetTrackedOptions(int trackedOptions, ReadOnlySpan<int> values, LingerOption? lingerOption)
        {
            if (lingerOption is not null)
            {
                SocketError errorCode = SocketPal.SetLingerOption(this, lingerOption);
                if (NetEventSource.Log.IsEnabled() && errorCode != SocketError.Success) NetEventSource.Info(this, $"SetLingerOption returned errorCode:{errorCode}");
            }

            for (int i = 0; i < values.Length; i++)
            {
                int mask = 1 << i;
                if ((trackedOptions & mask) == mask)
                {
                    TrackedSocketOptions tracked = (TrackedSocketOptions)(i + 1);
                    (SocketOptionName name, SocketOptionLevel level) = ToSocketOptions(tracked);
                    SocketError errorCode = SocketPal.SetSockOpt(this, level, name, values[i]);
                    if (NetEventSource.Log.IsEnabled() && errorCode != SocketError.Success) NetEventSource.Info(this, $"GetSockOpt({level},{name}) returned errorCode:{errorCode}");
                }
            }
        }

        private enum TrackedSocketOptions
        {
            None = 0,
            IP_TOS,
            IP_TTL,
            IPV6_PROTECTION_LEVEL,
            IPV6_V6ONLY,
            TCP_NODELAY,
            TCP_EXPEDITED_1122,
            TCP_KEEPALIVE,
            TCP_FASTOPEN,
            TCP_KEEPCNT,
            TCP_KEEPINTVL,
            SO_DEBUG,
            SO_ACCEPTCONN,
            SO_REUSEADDR,
            SO_KEEPALIVE,
            SO_DONTROUTE,
            SO_USELOOPBACK,
            SO_LINGER,
            SO_OOBINLINE,
            SO_DONTLINGER,
            SO_EXCLUSIVEADDRUSE,
            SO_SNDBUF,
            SO_RCVBUF,
            SO_SNDLOWAT,
            SO_RCVLOWAT,
            SO_SNDTIMEO,
            SO_RCVTIMEO,
        }

        internal static int LastTrackedSocketOptionIndex => (int)TrackedSocketOptions.SO_RCVTIMEO;

        private static TrackedSocketOptions ToTrackedSocketOptions(SocketOptionName name, SocketOptionLevel level)
            => ((int)name, level) switch
            {
                (3, SocketOptionLevel.IP) => TrackedSocketOptions.IP_TOS,
                (4, SocketOptionLevel.IP) => TrackedSocketOptions.IP_TTL,
                (23, SocketOptionLevel.IPv6) => TrackedSocketOptions.IPV6_PROTECTION_LEVEL,
                (27, SocketOptionLevel.IPv6) => TrackedSocketOptions.IPV6_V6ONLY,
                (1, SocketOptionLevel.Tcp) => TrackedSocketOptions.TCP_NODELAY,
                (2, SocketOptionLevel.Tcp) => TrackedSocketOptions.TCP_EXPEDITED_1122,
                (3, SocketOptionLevel.Tcp) => TrackedSocketOptions.TCP_KEEPALIVE,
                (15, SocketOptionLevel.Tcp) => TrackedSocketOptions.TCP_FASTOPEN,
                (16, SocketOptionLevel.Tcp) => TrackedSocketOptions.TCP_KEEPCNT,
                (17, SocketOptionLevel.Tcp) => TrackedSocketOptions.TCP_KEEPINTVL,
                (1, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_DEBUG,
                (2, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_ACCEPTCONN,
                (4, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_REUSEADDR,
                (8, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_KEEPALIVE,
                (16, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_DONTROUTE,
                (64, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_USELOOPBACK,
                (128, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_LINGER,
                (256, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_OOBINLINE,
                (-129, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_DONTLINGER,
                (-5, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_EXCLUSIVEADDRUSE,
                (4097, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_SNDBUF,
                (4098, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_RCVBUF,
                (4099, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_SNDLOWAT,
                (4100, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_RCVLOWAT,
                (4101, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_SNDTIMEO,
                (4102, SocketOptionLevel.Socket) => TrackedSocketOptions.SO_RCVTIMEO,

                _ => TrackedSocketOptions.None
            };

        private static (SocketOptionName, SocketOptionLevel) ToSocketOptions(TrackedSocketOptions options) =>
            options switch
            {
                TrackedSocketOptions.IP_TOS => ((SocketOptionName)3, SocketOptionLevel.IP),
                TrackedSocketOptions.IP_TTL => ((SocketOptionName)4, SocketOptionLevel.IP),
                TrackedSocketOptions.IPV6_PROTECTION_LEVEL => ((SocketOptionName)23, SocketOptionLevel.IPv6),
                TrackedSocketOptions.IPV6_V6ONLY => ((SocketOptionName)27, SocketOptionLevel.IPv6),
                TrackedSocketOptions.TCP_NODELAY => ((SocketOptionName)1, SocketOptionLevel.Tcp),
                TrackedSocketOptions.TCP_EXPEDITED_1122 => ((SocketOptionName)2, SocketOptionLevel.Tcp),
                TrackedSocketOptions.TCP_KEEPALIVE => ((SocketOptionName)3, SocketOptionLevel.Tcp),
                TrackedSocketOptions.TCP_FASTOPEN => ((SocketOptionName)15, SocketOptionLevel.Tcp),
                TrackedSocketOptions.TCP_KEEPCNT => ((SocketOptionName)16, SocketOptionLevel.Tcp),
                TrackedSocketOptions.TCP_KEEPINTVL => ((SocketOptionName)17, SocketOptionLevel.Tcp),
                TrackedSocketOptions.SO_DEBUG => ((SocketOptionName)1, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_ACCEPTCONN => ((SocketOptionName)2, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_REUSEADDR => ((SocketOptionName)4, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_KEEPALIVE => ((SocketOptionName)8, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_DONTROUTE => ((SocketOptionName)16, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_USELOOPBACK => ((SocketOptionName)64, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_LINGER => ((SocketOptionName)128, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_OOBINLINE => ((SocketOptionName)256, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_DONTLINGER => ((SocketOptionName)(-129), SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_EXCLUSIVEADDRUSE => ((SocketOptionName)(-5), SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_SNDBUF => ((SocketOptionName)4097, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_RCVBUF => ((SocketOptionName)4098, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_SNDLOWAT => ((SocketOptionName)4099, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_RCVLOWAT => ((SocketOptionName)4100, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_SNDTIMEO => ((SocketOptionName)4101, SocketOptionLevel.Socket),
                TrackedSocketOptions.SO_RCVTIMEO => ((SocketOptionName)4102, SocketOptionLevel.Socket),

                _ => default
            };
    }
}
