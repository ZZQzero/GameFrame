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
        private long binding;
        private int loginInFlight;
        private int closing;
        public override bool RequiresRestrictedTraffic => true;
        internal long Binding => Interlocked.Read(ref binding);

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
            bool login = IsAuthentication(header.Opcode);
            long current = Binding;
            bool allowed = current == 0 ? login && header.BodyLength <= 2048 &&
                Interlocked.CompareExchange(ref loginInFlight, 1, 0) == 0 && admitLogin(connection.RemoteEndpoint.Address) :
                !login && accounts.IsCurrent(current);
            return allowed ? ServerAdmission.Allow : ServerAdmission.Deny();
        }

        public override ServerAdmission CheckDispatch(ServerMessageHeader header)
        {
            bool login = IsAuthentication(header.Opcode);
            return Binding == 0 && login || accounts.IsCurrent(Binding) ? ServerAdmission.Allow : ServerAdmission.Deny();
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

            return current != 0 && !accounts.IsCurrent(current) ? (ushort)5 : null;
        }

        internal void Bind(long value)
        {
            Interlocked.Exchange(ref binding, value);
            if (value == 0)
            {
                connection.CloseAfterResponse(5);
            }
            else
            {
                connection.EnableFullTraffic();
            }
        }

        private static bool IsAuthentication(uint opcode) => opcode == GameNetRoutes.Login.Opcode ||
            opcode == GameNetRoutes.ResumeLogin.Opcode || opcode == GameNetRoutes.Register.Opcode;

        internal void FinishRegistration() => Interlocked.Exchange(ref loginInFlight, 0);

        internal void BeginLogout() => Volatile.Write(ref closing, 1);

        public override ValueTask ClosedAsync(ServerConnectionContext context, ServerCloseInfo result)
        {
            accounts.Detach(Binding);
            return ValueTask.CompletedTask;
        }
    }
}
