// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http
{
    public enum HttpRequestError
    {
        Unknown = 0,                            // Uncategorized/generic error

        NameResolutionError,                    // DNS request failed
        ConnectionError,                        // Transport-level error during connection
        TransportError,                         // Transport-level error after connection
        SecureConnectionError,                  // SSL/TLS error
        HttpProtocolError,                      // HTTP 2.0/3.0 protocol error occurred
        ExtendedConnectNotSupported,            // Extended CONNECT for WebSockets over HTTP/2 is not supported.
                                                // (SETTINGS_ENABLE_CONNECT_PROTOCOL has not been sent).
        VersionNegotiationError,                // Cannot negotiate the HTTP Version requested
        UserAuthenticationError,                // Authentication failed with the provided credentials
        ProxyTunnelError,

        InvalidResponse,                        // General error in response/malformed response
        ResponseEnded,                          // EOF received
        ConfigurationLimitExceeded,             // Response Content size exceeded MaxResponseContentBufferSize -or-
                                                // Response Header length exceeded MaxResponseHeadersLength -or-
                                                // any future limits are exceeded.
    }
}
