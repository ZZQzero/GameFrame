using System.Net;
using Game.Hotfix.Network.Generated;
using GameFrame.Network;
using GameFrame.Server.Network;

namespace Game.Server.Login
{
    internal sealed class LoginSession : ServerSession
    {
        private readonly AccountSessions accounts;
        private readonly LoginOptions options;
        private readonly Func<IPAddress, bool> admitLogin;
        private readonly ServerConnectionContext connection;
        private long deadline;
        private AccountSessions.Binding? binding;
        private int loginInFlight;
        private int closing;
        public override bool RequiresRestrictedTraffic => true;
        internal long Binding => Volatile.Read(ref binding)?.Id ?? 0;

        internal LoginSession(ServerConnectionContext connection, AccountSessions accounts, LoginOptions options, Func<IPAddress, bool> admitLogin)
        {
            this.accounts = accounts;
            this.options = options;
            this.admitLogin = admitLogin;
            this.connection = connection;
        }

        public override ValueTask ConnectedAsync(ServerConnectionContext context)
        {
            deadline = NetworkClock.GetMilliseconds() + (long)options.LoginTimeout.TotalMilliseconds;
            return ValueTask.CompletedTask;
        }

        public override ServerAdmission Admit(ServerMessageHeader header)
        {
            if (Volatile.Read(ref closing) != 0)
            {
                return ServerAdmission.Deny();
            }

            bool login = IsAuthentication(header.Opcode);
            AccountSessions.Binding? current = Volatile.Read(ref binding);
            if (current != null)
            {
                return !login && current.CloseReason == null ? ServerAdmission.Allow :
                    ServerAdmission.Deny(closeReason: current.CloseReason ?? 5);
            }

            if (!login || header.BodyLength > 2048 || Interlocked.CompareExchange(ref loginInFlight, 1, 0) != 0)
            {
                return ServerAdmission.Deny();
            }

            if (!admitLogin(connection.RemoteEndpoint.Address))
            {
                FinishAuthentication();
                return ServerAdmission.Deny(1, 1);
            }

            return ServerAdmission.Allow;
        }

        public override ServerAdmission CheckDispatch(ServerMessageHeader header)
        {
            if (Volatile.Read(ref closing) != 0)
            {
                return ServerAdmission.Deny();
            }

            bool login = IsAuthentication(header.Opcode);
            AccountSessions.Binding? current = Volatile.Read(ref binding);
            if (current == null)
            {
                return login ? ServerAdmission.Allow : ServerAdmission.Deny();
            }

            return !login && current.CloseReason == null ? ServerAdmission.Allow :
                ServerAdmission.Deny(closeReason: current.CloseReason ?? 5);
        }

        public override ushort? CheckDeadline(long nowMilliseconds)
        {
            if (Volatile.Read(ref closing) != 0)
            {
                return null;
            }

            long current = Binding;
            if (current == 0 && nowMilliseconds >= deadline)
            {
                return 6;
            }

            return Volatile.Read(ref binding)?.CloseReason;
        }

        internal void Bind(AccountSessions.Binding? value)
        {
            Interlocked.Exchange(ref binding, value);
            if (value == null)
            {
                Volatile.Write(ref closing, 1);
                connection.CloseAfterResponse(5);
            }
            else if (value.CloseReason is ushort reason)
            {
                connection.RequestClose(reason);
            }
            else
            {
                connection.EnableFullTraffic();
            }
        }

        private static bool IsAuthentication(uint opcode) => opcode == GameNetRoutes.Login.Opcode ||
            opcode == GameNetRoutes.ResumeLogin.Opcode || opcode == GameNetRoutes.Register.Opcode;

        internal void FinishAuthentication() => Interlocked.Exchange(ref loginInFlight, 0);

        internal void BeginClosing() => Volatile.Write(ref closing, 1);

        public override ValueTask ClosedAsync(ServerConnectionContext context, ServerCloseInfo result)
        {
            accounts.Detach(Binding);
            return ValueTask.CompletedTask;
        }
    }
}
