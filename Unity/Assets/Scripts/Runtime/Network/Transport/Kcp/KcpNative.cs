#nullable disable
using System;
using System.Runtime.InteropServices;

namespace GameFrame.Network.Transport.Kcp
{
    // All calls, including destroy and listener child operations, run on the handle's creating thread.
    internal static class KcpNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Library = "__Internal";
#else
        private const string Library = "gameframe_kcp";
#endif
        internal const int Backpressure = -3;
        internal const int Established = 2;
        internal const int PeerClosed = 3;
        internal const int Fatal = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Config
        {
            internal uint Size, Version, Role, Conv, NativeLimit, ReliableLimit, UnreliableLimit, Reserved;
            internal IntPtr Identity;
            internal uint IdentityLength, ReservedIdentity;
            internal IntPtr Psk;
            internal uint PskLength, ReservedPsk;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Stats
        {
            internal uint Size, Version, State, Capabilities;
            internal int FatalCode, NativeError;
            internal ulong OwnedBytes, PeakBytes, Allocations, ReceivedBytes, PolledBytes, ReceivedPackets, PolledPackets;
            internal ulong RawDropped, UnreliableDropped;
            internal uint InputCount, OutputCount, OutputBytes, OutputOldestMs, Waitsnd, SrttMs, RtoMs;
            internal uint SendWindow, ReceiveWindow, RemoteWindow, PlaintextCount, PlaintextBytes;
            internal ulong PendingReliableBytes, InflightReliableBytes, TimeoutRetransmits;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Peer
        {
            internal uint AddressFamily;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] Address;
            internal ushort Port, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ListenerConfig
        {
            internal uint Size, Version, NativeLimit, ConnectionLimit, MaxConnections, MaxHandshakes, MaxGrants, Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Grant
        {
            internal uint Size, Version, Conv, RemainingTtlMs;
            internal ulong Id;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] Identity;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Psk;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] TicketDigest;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ConnectionEvent
        {
            internal uint Size, Version, Kind, Reason;
            internal ulong Id, Generation, GrantId;
            internal IntPtr Connection;
            internal Peer Peer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UdpMetadata
        {
            internal uint Size, Version, EnqueuedMs, Reserved;
            internal ulong Id, Generation;
            internal Peer Peer;
            internal uint ReservedTail1, ReservedTail2;
        }

        internal static void ValidateAbi()
        {
            if (IntPtr.Size != 8 || Marshal.SizeOf<Config>() != 64 || Marshal.SizeOf<Stats>() != 168 ||
                Marshal.SizeOf<Peer>() != 24 || Marshal.SizeOf<ListenerConfig>() != 32 ||
                Marshal.SizeOf<Grant>() != 104 || Marshal.SizeOf<ConnectionEvent>() != 72 ||
                Marshal.SizeOf<UdpMetadata>() != 64 || Marshal.OffsetOf<Config>(nameof(Config.Identity)).ToInt32() != 32 ||
                Marshal.OffsetOf<Config>(nameof(Config.Psk)).ToInt32() != 48 || AbiVersion() != 1)
            {
                throw new NetworkException("gameframe_kcp ABI/architecture mismatch (64-bit ABI 1 required).");
            }
        }

        internal static void Check(int result, string operation)
        {
            if (result < 0)
            {
                throw new NetworkException($"Native KCP {operation} failed ({result}).");
            }
        }

        internal static IntPtr Create(NetworkConnectOptions options, int nativeBytes)
        {
            GCHandle identity = default;
            GCHandle psk = default;
            try
            {
                identity = GCHandle.Alloc(options.PskIdentity, GCHandleType.Pinned);
                psk = GCHandle.Alloc(options.Psk, GCHandleType.Pinned);
                var config = new Config
                {
                    Size = 64, Version = 1, Role = 1, Conv = options.Conv,
                    NativeLimit = checked((uint)nativeBytes), ReliableLimit = (uint)options.ReliableLimit,
                    UnreliableLimit = (uint)options.UnreliableLimit, Identity = identity.AddrOfPinnedObject(),
                    IdentityLength = 16, Psk = psk.AddrOfPinnedObject(), PskLength = 32
                };
                Check(CreateNative(ref config, out IntPtr handle), "create");
                return handle;
            }
            finally
            {
                if (psk.IsAllocated)
                {
                    psk.Free();
                }

                if (identity.IsAllocated)
                {
                    identity.Free();
                }
            }
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_abi_version")]
        internal static extern uint AbiVersion();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_create")]
        internal static extern int CreateNative(ref Config config, out IntPtr connection);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_feed_udp")]
        internal static extern int Feed(IntPtr connection, [In] byte[] bytes, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_tick")]
        internal static extern int Tick(IntPtr connection, uint now);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_send_reliable")]
        internal static extern int SendReliable(IntPtr connection, [In] byte[] bytes, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_send_unreliable")]
        internal static extern int SendUnreliable(IntPtr connection, [In] byte[] bytes, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_poll_udp")]
        internal static extern int PollUdp(IntPtr connection, [Out] byte[] bytes, int capacity, out uint born);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_peek_frame")]
        internal static extern int Peek(IntPtr connection, out int channel, out int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_peek_frame_header")]
        internal static extern int PeekHeader(IntPtr connection, [Out] byte[] bytes, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_recv_frame")]
        internal static extern int Receive(IntPtr connection, [Out] byte[] bytes, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_get_stats")]
        internal static extern int GetStats(IntPtr connection, ref Stats stats);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_destroy")]
        internal static extern int Destroy(IntPtr connection);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_create")]
        internal static extern int ListenerCreate(ref ListenerConfig config, out IntPtr listener);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_register_grant")]
        internal static extern int RegisterGrant(IntPtr listener, ref Grant grant, uint now);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_revoke_grant")]
        internal static extern int RevokeGrant(IntPtr listener, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_feed_udp")]
        internal static extern int ListenerFeed(IntPtr listener, ref Peer peer, [In] byte[] bytes, int length, uint now);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_tick")]
        internal static extern int ListenerTick(IntPtr listener, uint now);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_poll_udp")]
        internal static extern int ListenerPollUdp(IntPtr listener, [Out] byte[] bytes, int capacity, ref UdpMetadata metadata);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_poll_event")]
        internal static extern int ListenerPollEvent(IntPtr listener, ref ConnectionEvent connectionEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_confirm_binding")]
        internal static extern int ConfirmBinding(IntPtr listener, ulong id, ulong generation, [In] byte[] ticket, int length, uint now);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_reject_binding")]
        internal static extern int RejectBinding(IntPtr listener, ulong id, ulong generation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_get_connection_state")]
        internal static extern int ConnectionState(IntPtr listener, ulong id, ulong generation, out int state);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_close_connection")]
        internal static extern int CloseConnection(IntPtr listener, ulong id, ulong generation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_release_connection")]
        internal static extern int ReleaseConnection(IntPtr listener, ulong id, ulong generation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gfnet_listener_destroy")]
        internal static extern int ListenerDestroy(IntPtr listener);
    }
}
