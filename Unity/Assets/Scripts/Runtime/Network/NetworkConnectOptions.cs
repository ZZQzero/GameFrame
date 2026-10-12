using System;
using System.Diagnostics;

namespace GameFrame.Network
{
    /// <summary>凭据数组属于调用者；Connect 完成前不修改，使用后由会话所有者清零。</summary>
    public sealed class NetworkConnectOptions
    {
        public NetworkTransportKind Transport { get; }
        public Uri Endpoint { get; }
        public bool AllowDevelopmentWebSocket { get; }
        public int ReliableLimit { get; }
        public int UnreliableLimit { get; }
        public byte[] Ticket { get; }
        public uint Conv { get; }
        public byte[] PskIdentity { get; }
        public byte[] Psk { get; }
        public long GrantDeadlineMilliseconds { get; }

        private NetworkConnectOptions(NetworkTransportKind transport, Uri endpoint, bool allowDevelopmentWebSocket,
            int reliableLimit, int unreliableLimit, byte[] ticket, uint conv, byte[] pskIdentity, byte[] psk,
            long grantDeadlineMilliseconds)
        {
            Transport = transport;
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            AllowDevelopmentWebSocket = allowDevelopmentWebSocket;
            ReliableLimit = reliableLimit;
            UnreliableLimit = unreliableLimit;
            Ticket = ticket;
            Conv = conv;
            PskIdentity = pskIdentity;
            Psk = psk;
            GrantDeadlineMilliseconds = grantDeadlineMilliseconds;
            ValidateStructure();
        }

        public static NetworkConnectOptions WebSocket(Uri endpoint, bool allowDevelopmentWebSocket = false,
            int reliableLimit = 32768)
        {
            return new NetworkConnectOptions(NetworkTransportKind.WebSocket, endpoint, allowDevelopmentWebSocket,
                reliableLimit, 0, Array.Empty<byte>(), 0, Array.Empty<byte>(), Array.Empty<byte>(), 0);
        }

        public static NetworkConnectOptions Kcp(Uri endpoint, byte[] ticket, uint conv, byte[] pskIdentity,
            byte[] psk, long grantDeadlineMilliseconds, int reliableLimit = 32768, int unreliableLimit = 1150)
        {
            return new NetworkConnectOptions(NetworkTransportKind.Kcp, endpoint, false, reliableLimit, unreliableLimit,
                ticket, conv, pskIdentity, psk, grantDeadlineMilliseconds);
        }

        internal void Validate(NetworkClientOptions client, NetProtocolCatalog catalog, long nowMilliseconds)
        {
            ValidateStructure();
            Uri[] trusted = Transport == NetworkTransportKind.WebSocket ? client.TrustedWebSocketEndpoints : client.TrustedUdpEndpoints;
            if (!Array.Exists(trusted, endpoint => Endpoint.Equals(endpoint)))
            {
                throw new ArgumentException("Endpoint is not present in the environment's trusted endpoint configuration.");
            }

            if (catalog.MaxReliableBodyBytes + 32 > ReliableLimit ||
                (UnreliableLimit != 0 && catalog.MaxUnreliableBodyBytes + 32 > UnreliableLimit))
            {
                throw new ArgumentException("Proposed frame limits cannot carry the registered catalog.");
            }

            if (Transport == NetworkTransportKind.Kcp)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                throw new PlatformNotSupportedException("KCP/DTLS is not supported in WebGL.");
#else
                if (nowMilliseconds >= GrantDeadlineMilliseconds)
                {
                    throw new NetworkException("KCP grant has expired.");
                }

                client.ValidateKcpNegotiation(ReliableLimit, UnreliableLimit);
#endif
            }
        }

        private void ValidateStructure()
        {
            if (!Endpoint.IsAbsoluteUri || Endpoint.UserInfo.Length != 0 || Endpoint.Fragment.Length != 0 ||
                ReliableLimit < 2048 || ReliableLimit > 32768)
            {
                throw new ArgumentException("Invalid endpoint or reliable limit.");
            }

            if (Transport == NetworkTransportKind.WebSocket)
            {
                if (Endpoint.Scheme == "wss")
                {
                    return;
                }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (Endpoint.Scheme == "ws" && AllowDevelopmentWebSocket)
                {
                    return;
                }
#endif
                throw new ArgumentException("A secure wss endpoint is required; development ws needs an explicit development configuration.");
            }

            if (Endpoint.Scheme != "udp" || Endpoint.Port < 1 || Endpoint.Port > 65535 ||
                Ticket == null || Ticket.Length < 1 || Ticket.Length > 1024 || Conv == 0 ||
                PskIdentity == null || PskIdentity.Length != 16 || Psk == null || Psk.Length != 32 ||
                GrantDeadlineMilliseconds <= 0 || (UnreliableLimit != 0 && (UnreliableLimit < 256 || UnreliableLimit > 1150)))
            {
                throw new ArgumentException("Invalid KCP endpoint, grant credentials or channel limits.");
            }
        }
    }
}
