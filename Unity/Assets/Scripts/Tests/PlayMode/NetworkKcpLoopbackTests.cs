using System;
using System.Collections;
using System.IO;
using Cysharp.Threading.Tasks;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    public sealed class NetworkKcpLoopbackTests
    {
        [Serializable]
        private sealed class Credentials
        {
            public int Conv;
            public int Port;
            public string Identity;
            public string Psk;
            public string Ticket;
        }

        private static byte[] DecodeHex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        [UnityTearDown]
        public IEnumerator Shutdown() => UniTask.ToCoroutine(async () => await GameRuntime.ShutdownAsync());

        [UnityTest]
        public IEnumerator NativeUdpHelloEchoAndRelease() => UniTask.ToCoroutine(async () =>
        {
            string path = Environment.GetEnvironmentVariable("GF_KCP_LOOPBACK_CREDENTIALS");
            if (string.IsNullOrEmpty(path))
            {
                Assert.Ignore("Start the explicit local-kcp-dtls host and configure temporary GF_KCP_LOOPBACK_CREDENTIALS.");
            }

            Credentials credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(path));
            byte[] identity = DecodeHex(credentials.Identity);
            byte[] psk = DecodeHex(credentials.Psk);
            byte[] ticket = DecodeHex(credentials.Ticket);
            credentials.Identity = credentials.Psk = credentials.Ticket = null;
            try
            {
                await GameRuntime.InitAsync(new GameRuntimeConfig
                {
                    EnableTimer = false, EnableUI = false, NetworkOptions = new NetworkRuntimeOptions()
                });
                var endpoint = new Uri($"udp://127.0.0.1:{credentials.Port}");
                NetworkClient client = GameRuntime.Network.CreateClient(new NetworkClientOptions
                {
                    TrustedUdpEndpoints = new[] { endpoint }
                }, GameNetProtocol.Catalog);
                await client.ConnectAsync(NetworkConnectOptions.Kcp(endpoint, ticket, (uint)credentials.Conv,
                    identity, psk, NetworkClock.GetMilliseconds() + 10000));
                Assert.AreEqual(NetworkClientState.Connected, client.State);
                Assert.IsTrue(client.GetMetrics().Kcp.Available);
                EchoResponse echo = await client.RequestAsync(GameNetRoutes.Echo, new EchoRequest { Text = "Unity native UDP" }, TimeSpan.FromSeconds(2));
                Assert.AreEqual("Unity native UDP", echo.Text);
                await client.CloseAsync();
                Assert.AreEqual(0, client.GetMetrics().OwnedBytes);
                Assert.AreEqual(0, client.GetMetrics().Kcp.NativeReservedBytes);
                await client.DisposeAsync();
            }
            finally
            {
                Array.Clear(identity, 0, identity.Length);
                Array.Clear(psk, 0, psk.Length);
                Array.Clear(ticket, 0, ticket.Length);
            }
        });
    }
}
