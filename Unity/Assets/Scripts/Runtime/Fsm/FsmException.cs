using System;

namespace GameFrame.Fsm
{
    public sealed class FsmException : InvalidOperationException
    {
        public FsmException(string message) : base(message)
        {
        }
    }
}
