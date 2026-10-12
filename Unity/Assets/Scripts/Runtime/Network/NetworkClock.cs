using System;
using System.Diagnostics;

namespace GameFrame.Network
{
    public static class NetworkClock
    {
        public static long GetMilliseconds()
        {
            long timestamp = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            return timestamp / frequency * 1000 + timestamp % frequency * 1000 / frequency;
        }
    }

}
