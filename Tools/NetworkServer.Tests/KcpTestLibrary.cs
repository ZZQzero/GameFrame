using System.Runtime.InteropServices;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    internal static class KcpTestLibrary
    {
        private static readonly object Gate = new object();
        private static string? loadedPath;

        internal static void Load()
        {
            string? path = Environment.GetEnvironmentVariable("GF_KCP_NATIVE_LIBRARY");
            if (string.IsNullOrEmpty(path))
            {
                Assert.Ignore("Set GF_KCP_NATIVE_LIBRARY to a verified host build to run native UDP integration.");
            }

            path = Path.GetFullPath(path!);
            lock (Gate)
            {
                if (loadedPath != null && loadedPath != path)
                {
                    throw new InvalidOperationException("A different KCP library is already loaded in this test process.");
                }

                if (loadedPath == null)
                {
                    IntPtr library = NativeLibrary.Load(path);
                    NativeLibrary.SetDllImportResolver(typeof(KcpNative).Assembly, (name, _, _) =>
                        name == "gameframe_kcp" ? library : IntPtr.Zero);
                    loadedPath = path;
                }

                KcpNative.ValidateAbi();
            }
        }
    }
}
