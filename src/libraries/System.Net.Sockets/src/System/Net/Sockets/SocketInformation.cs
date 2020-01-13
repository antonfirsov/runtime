// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Serialization;

namespace System.Net.Sockets
{
    [Serializable]
    public struct SocketInformation
    {
        public byte[] ProtocolInformation { get; set; }
        public SocketInformationOptions Options { get; set; }


        internal bool IsNonBlocking{
            get{
                return ((Options&SocketInformationOptions.NonBlocking)!=0);
            }
            set{
                if (value){
                    Options |= SocketInformationOptions.NonBlocking;
                }
                else{
                    Options &= ~SocketInformationOptions.NonBlocking;
                }
            }
        }

        internal bool IsConnected{
            get{
                return ((Options&SocketInformationOptions.Connected)!=0);
            }
            set{
                if (value){
                    Options |= SocketInformationOptions.Connected;
                }
                else{
                    Options &= ~SocketInformationOptions.Connected;
                }
            }
        }

        internal bool IsListening{
            get{
                return ((Options&SocketInformationOptions.Listening)!=0);
            }
            set{
                if (value){
                    Options |= SocketInformationOptions.Listening;
                }
                else{
                    Options &= ~SocketInformationOptions.Listening;
                }
            }
        }

        internal bool UseOnlyOverlappedIO{
            get{
                return ((Options&SocketInformationOptions.UseOnlyOverlappedIO)!=0);
            }
            set{
                if (value){
                    Options |= SocketInformationOptions.UseOnlyOverlappedIO;
                }
                else{
                    Options &= ~SocketInformationOptions.UseOnlyOverlappedIO;
                }
            }
        }
    }
}
