#nullable disable
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace GameFrame.Network
{
    internal static class WebSocketFrameTransport
    {
        // Never let a fragment allocate the peer's declared body before validating the fixed header.
        public static async Task<OwnedFrame> ReceiveAsync(WebSocket socket, FrameMemoryBudget budget,
            int frameLimit, FrameDirection direction, bool handshaking, CancellationToken cancellationToken, Action<FrameHeader> validateHeader = null)
        {
            OwnedFrame frame = budget.Allocate(FrameCodec.HeaderBytes);
            bool transferred = false;
            try
            {
                int position = 0;
                int expected = FrameCodec.HeaderBytes;
                bool hasHeader = false;
                while (true)
                {
                    var segment = new ArraySegment<byte>(frame.Bytes, position, expected - position);
                    WebSocketReceiveResult result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (position != 0)
                        {
                            throw new NetworkProtocolException("WebSocket closed in a fragmented application frame.");
                        }

                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        throw new NetworkProtocolException("Expected exactly one binary application frame.");
                    }

                    position += result.Count;
                    if (!hasHeader && position == FrameCodec.HeaderBytes)
                    {
                        FrameHeader header = FrameCodec.ReadHeader(frame.Bytes, frameLimit, NetDelivery.Reliable,
                            direction, handshaking);
                        validateHeader?.Invoke(header);
                        expected = checked(FrameCodec.HeaderBytes + header.BodyLength);
                        if (expected > frame.Capacity)
                        {
                            // Reserve old + new at the expansion peak before releasing the fixed header.
                            OwnedFrame expanded = budget.Allocate(expected);
                            frame.Bytes.CopyTo(expanded.Bytes, 0);
                            frame.Dispose();
                            frame = expanded;
                        }

                        frame.Header = header;
                        hasHeader = true;
                    }

                    if (result.EndOfMessage)
                    {
                        if (!hasHeader || position != expected)
                        {
                            throw new NetworkProtocolException("Truncated WebSocket application frame.");
                        }

                        frame.Length = position;
                        transferred = true;
                        return frame;
                    }

                    if (position == expected)
                    {
                        throw new NetworkProtocolException("Application frame boundary requires EndOfMessage.");
                    }
                }
            }
            finally
            {
                if (!transferred)
                {
                    frame.Dispose();
                }
            }
        }

        public static Task SendAsync(WebSocket socket, OwnedFrame frame, CancellationToken cancellationToken)
        {
            return socket.SendAsync(new ArraySegment<byte>(frame.Bytes, 0, frame.Length),
                WebSocketMessageType.Binary, true, cancellationToken);
        }
    }
}
