using System;
using Game.Protocol;

namespace Game.Hotfix
{
    /// <summary>认证结果与可选的账号记忆结果分别报告；不包含认证凭据。</summary>
    public sealed class GameLoginResult
    {
        public ResultCode ResultCode { get; }
        public Exception RememberedLoginError { get; }

        internal GameLoginResult(ResultCode resultCode, Exception rememberedLoginError = null)
        {
            ResultCode = resultCode;
            RememberedLoginError = rememberedLoginError;
        }
    }
}
