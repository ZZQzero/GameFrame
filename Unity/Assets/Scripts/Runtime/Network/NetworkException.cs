using System;

namespace GameFrame.Network
{
    public class NetworkException : Exception
    {
        public NetworkException(string message) : base(message)
        {
        }

        public NetworkException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class NetworkProtocolException : NetworkException
    {
        public NetworkProtocolException(string message) : base(message)
        {
        }

        public NetworkProtocolException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class NetworkBackpressureException : NetworkException
    {
        public NetworkBackpressureException(string message) : base(message)
        {
        }
    }

    public sealed class NetworkRequestTimeoutException : NetworkException
    {
        public NetworkRequestTimeoutException(string message) : base(message)
        {
        }
    }

    public sealed class NetworkRemoteException : NetworkException
    {
        public int StatusCode { get; }

        public NetworkRemoteException(int statusCode) : base($"Remote RPC failed with status {statusCode}.")
        {
            if (statusCode == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            }

            StatusCode = statusCode;
        }
    }
}
