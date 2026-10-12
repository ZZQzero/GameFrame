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
        internal const uint Version = 2;
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
            if (IntPtr.Size != 8 || AbiVersion() != Version)
            {
                throw new NetworkException("gameframe_kcp ABI/architecture mismatch (64-bit ABI 2 required).");
            }
            ValidateLayout<Config>(64,
                new[] { nameof(Config.Size), nameof(Config.Version), nameof(Config.Role), nameof(Config.Conv), nameof(Config.NativeLimit), nameof(Config.ReliableLimit), nameof(Config.UnreliableLimit), nameof(Config.Reserved), nameof(Config.Identity), nameof(Config.IdentityLength), nameof(Config.ReservedIdentity), nameof(Config.Psk), nameof(Config.PskLength), nameof(Config.ReservedPsk) },
                new[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 44, 48, 56, 60 });
            ValidateLayout<Stats>(168,
                new[] { nameof(Stats.Size), nameof(Stats.Version), nameof(Stats.State), nameof(Stats.Capabilities), nameof(Stats.FatalCode), nameof(Stats.NativeError), nameof(Stats.OwnedBytes), nameof(Stats.PeakBytes), nameof(Stats.Allocations), nameof(Stats.ReceivedBytes), nameof(Stats.PolledBytes), nameof(Stats.ReceivedPackets), nameof(Stats.PolledPackets), nameof(Stats.RawDropped), nameof(Stats.UnreliableDropped), nameof(Stats.InputCount), nameof(Stats.OutputCount), nameof(Stats.OutputBytes), nameof(Stats.OutputOldestMs), nameof(Stats.Waitsnd), nameof(Stats.SrttMs), nameof(Stats.RtoMs), nameof(Stats.SendWindow), nameof(Stats.ReceiveWindow), nameof(Stats.RemoteWindow), nameof(Stats.PlaintextCount), nameof(Stats.PlaintextBytes), nameof(Stats.PendingReliableBytes), nameof(Stats.InflightReliableBytes), nameof(Stats.TimeoutRetransmits) },
                new[] { 0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 152, 160 });
            ValidateLayout<Peer>(24,
                new[] { nameof(Peer.AddressFamily), nameof(Peer.Address), nameof(Peer.Port), nameof(Peer.Reserved) },
                new[] { 0, 4, 20, 22 });
            ValidateLayout<ListenerConfig>(32,
                new[] { nameof(ListenerConfig.Size), nameof(ListenerConfig.Version), nameof(ListenerConfig.NativeLimit), nameof(ListenerConfig.ConnectionLimit), nameof(ListenerConfig.MaxConnections), nameof(ListenerConfig.MaxHandshakes), nameof(ListenerConfig.MaxGrants), nameof(ListenerConfig.Reserved) },
                new[] { 0, 4, 8, 12, 16, 20, 24, 28 });
            ValidateLayout<Grant>(104,
                new[] { nameof(Grant.Size), nameof(Grant.Version), nameof(Grant.Conv), nameof(Grant.RemainingTtlMs), nameof(Grant.Id), nameof(Grant.Identity), nameof(Grant.Psk), nameof(Grant.TicketDigest) },
                new[] { 0, 4, 8, 12, 16, 24, 40, 72 });
            ValidateLayout<ConnectionEvent>(72,
                new[] { nameof(ConnectionEvent.Size), nameof(ConnectionEvent.Version), nameof(ConnectionEvent.Kind), nameof(ConnectionEvent.Reason), nameof(ConnectionEvent.Id), nameof(ConnectionEvent.Generation), nameof(ConnectionEvent.GrantId), nameof(ConnectionEvent.Connection), nameof(ConnectionEvent.Peer) },
                new[] { 0, 4, 8, 12, 16, 24, 32, 40, 48 });
            ValidateLayout<UdpMetadata>(64,
                new[] { nameof(UdpMetadata.Size), nameof(UdpMetadata.Version), nameof(UdpMetadata.EnqueuedMs), nameof(UdpMetadata.Reserved), nameof(UdpMetadata.Id), nameof(UdpMetadata.Generation), nameof(UdpMetadata.Peer), nameof(UdpMetadata.ReservedTail1), nameof(UdpMetadata.ReservedTail2) },
                new[] { 0, 4, 8, 12, 16, 24, 32, 56, 60 });
        }

        private static void ValidateLayout<T>(int size, string[] fields, int[] offsets) where T : struct
        {
            if (Marshal.SizeOf<T>() != size)
            {
                throw new NetworkException($"Native ABI size mismatch for {typeof(T).Name}.");
            }
            for (int i = 0; i < fields.Length; i++)
            {
                if (Marshal.OffsetOf<T>(fields[i]).ToInt32() != offsets[i])
                {
                    throw new NetworkException($"Native ABI offset mismatch for {typeof(T).Name}.{fields[i]}.");
                }
            }
        }

        internal static void Check(int result, string operation)
        {
            if (result < 0)
            {
                throw new NetworkException($"Native KCP {operation} failed ({result}).");
            }
        }

        internal static IntPtr Create(uint conv, byte[] identityBytes, byte[] pskBytes, int reliableLimit, int unreliableLimit, int nativeBytes)
        {
            GCHandle identity = default;
            GCHandle psk = default;
            try
            {
                identity = GCHandle.Alloc(identityBytes, GCHandleType.Pinned);
                psk = GCHandle.Alloc(pskBytes, GCHandleType.Pinned);
                var config = new Config
                {
                    Size = 64, Version = KcpNative.Version, Role = 1, Conv = conv,
                    NativeLimit = checked((uint)nativeBytes), ReliableLimit = (uint)reliableLimit,
                    UnreliableLimit = (uint)unreliableLimit, Identity = identity.AddrOfPinnedObject(),
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
        internal static extern int Peek(IntPtr connection, out int channel, out int length, out uint born);
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
