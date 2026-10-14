using System;
using Cysharp.Threading.Tasks;

namespace Game.Hotfix
{
    public sealed class LoginPanelArgs
    {
        public GameLoginSession Session { get; }
        public Func<string, UniTask> LoggedIn { get; }
        public string Message { get; }

        public LoginPanelArgs(GameLoginSession session, Func<string, UniTask> loggedIn, string message = "")
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            LoggedIn = loggedIn ?? throw new ArgumentNullException(nameof(loggedIn));
            Message = message;
        }
    }
}
