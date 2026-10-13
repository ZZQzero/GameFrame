using System.Net;
using GameFrame.Network;

namespace Game.Server.Login
{
    internal sealed class AuthenticationRateLimiter
    {
        private readonly object gate = new object();
        private readonly int maxAttemptsPerSecond;
        private readonly Dictionary<IPAddress, (long Window, int Attempts)> ipAttempts = new Dictionary<IPAddress, (long, int)>();
        private readonly Dictionary<string, (long Window, int Attempts)> accountAttempts = new Dictionary<string, (long, int)>(StringComparer.Ordinal);

        internal AuthenticationRateLimiter(LoginOptions options)
        {
            options.Validate();
            maxAttemptsPerSecond = options.MaxLoginAttemptsPerSecond;
        }

        internal bool AdmitIp(IPAddress address) => Admit(ipAttempts, address);
        internal bool AdmitAccount(string account) => Admit(accountAttempts, account);

        private bool Admit<TKey>(Dictionary<TKey, (long Window, int Attempts)> attempts, TKey key) where TKey : notnull
        {
            lock (gate)
            {
                long window = NetworkClock.GetMilliseconds() / 1000;
                if (!attempts.TryGetValue(key, out var value) && attempts.Count >= 128)
                {
                    foreach (TKey expired in attempts.Where(pair => window - pair.Value.Window > 10).Select(pair => pair.Key).ToArray())
                    {
                        attempts.Remove(expired);
                    }

                    if (attempts.Count >= 128)
                    {
                        return false;
                    }
                }

                int count = value.Window == window ? value.Attempts + 1 : 1;
                attempts[key] = (window, count);
                return count <= maxAttemptsPerSecond;
            }
        }
    }
}
