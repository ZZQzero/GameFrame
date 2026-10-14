using System.Net;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using Game.Server.Gomoku;
using Game.Server.Login;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Server.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class GomokuRoomTests
    {
        private GomokuRooms rooms = null!;
        private readonly FrameMemoryBudget memory = new FrameMemoryBudget(1024 * 1024);
        private long nextBinding;
        private readonly List<(AccountSessions.Binding Binding, ServerConnectionContext Connection)> players = new();

        [SetUp]
        public void Start()
        {
            rooms = new GomokuRooms();
            nextBinding = 0;
        }

        [TearDown]
        public async Task Stop()
        {
            await rooms.DisposeAsync();
            players.Clear();
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        private (AccountSessions.Binding Binding, ServerConnectionContext Connection) Player(ulong account = 0)
        {
            long id = ++nextBinding;
            var binding = new AccountSessions.Binding(id, account == 0 ? (ulong)id : account, (ulong)id,
                NetworkClock.GetMilliseconds() + 60000);
            var connection = new ServerConnectionContext((ulong)id, 1, 0, NetworkTransportKind.WebSocket,
                new IPEndPoint(IPAddress.Loopback, 1), GameNetProtocol.Catalog, memory, default,
                frame =>
                {
                    frame.Dispose();
                    return true;
                }, () => { });
            players.Add((binding, connection));
            return (binding, connection);
        }

        private GomokuResponse Command(int player, GomokuOperation operation, ulong id,
            GomokuSnapshot? snapshot = null, string code = "", bool ready = false, int column = 0, int row = 0)
        {
            var peer = players[player];
            return rooms.Execute(peer.Binding, peer.Connection, operation, new GomokuCommand
            {
                CommandId = id, RoomId = snapshot?.RoomId ?? "", ExpectedVersion = snapshot?.Version ?? 0,
                RoomCode = code, Ready = ready, Column = column, Row = row
            });
        }

        private GomokuSnapshot Current(int player) => rooms.Query(players[player].Binding, players[player].Connection, 0).Snapshot!;

        [Test]
        public async Task LastSeatHasOneWinnerAndAccountCannotOccupyTwoRooms()
        {
            Player();
            Player();
            Player();
            GomokuSnapshot first = Command(0, GomokuOperation.Create, 1).Snapshot!;
            Assert.That(first.RoomCode, Does.Match("^[0-9]{8}$"));
            var attempts = await Task.WhenAll(
                Task.Run(() => Command(1, GomokuOperation.Join, 1, code: first.RoomCode)),
                Task.Run(() => Command(2, GomokuOperation.Join, 1, code: first.RoomCode)));
            Assert.That(attempts.Count(r => r.Result == GomokuResult.Ok), Is.EqualTo(1));
            Assert.That(attempts.Count(r => r.Result == GomokuResult.RoomFull), Is.EqualTo(1));
            Assert.That(Current(0).Members.Length, Is.EqualTo(2));
            Assert.That(Command(0, GomokuOperation.Create, 2).Result, Is.EqualTo(GomokuResult.AlreadyInRoom));
            Assert.That(rooms.RoomCount, Is.EqualTo(1));
        }

        [Test]
        public void RulesAreAuthoritativeAndDuplicateCommandsCannotCommitTwice()
        {
            Player();
            Player();
            GomokuSnapshot snapshot = Command(0, GomokuOperation.Create, 1).Snapshot!;
            Assert.That(Command(0, GomokuOperation.Start, 2, snapshot).Result, Is.EqualTo(GomokuResult.NotReady));
            Command(1, GomokuOperation.Join, 1, code: snapshot.RoomCode);
            Command(0, GomokuOperation.Ready, 3, Current(0), ready: true);
            Command(1, GomokuOperation.Ready, 2, Current(1), ready: true);
            Assert.That(Command(1, GomokuOperation.Start, 3, Current(1)).Result, Is.EqualTo(GomokuResult.NotHost));
            Assert.That(Command(0, GomokuOperation.Start, 4, Current(0)).Result, Is.EqualTo(GomokuResult.Ok));
            snapshot = Current(0);
            Assert.That(Command(1, GomokuOperation.Move, 4, Current(1)).Result, Is.EqualTo(GomokuResult.InvalidMove));
            Assert.That(Command(0, GomokuOperation.Move, 5, snapshot, column: -1).Result, Is.EqualTo(GomokuResult.InvalidMove));
            Assert.That(Command(0, GomokuOperation.Move, 6, snapshot, column: 0, row: 0).Result, Is.EqualTo(GomokuResult.Ok));
            Assert.That(Command(0, GomokuOperation.Move, 6, snapshot, column: 0, row: 0).Result, Is.EqualTo(GomokuResult.Ok));
            Assert.That(Command(0, GomokuOperation.Move, 6, snapshot, column: 1, row: 0).Result, Is.EqualTo(GomokuResult.InvalidCommand));
            Assert.That(Current(0).Moves.Length, Is.EqualTo(1));
            Assert.That(Command(1, GomokuOperation.Move, 5, snapshot, column: 1).Result, Is.EqualTo(GomokuResult.StaleVersion));
            ulong a = 7;
            ulong b = 6;
            for (int i = 1; i < 5; i++)
            {
                Assert.That(Command(1, GomokuOperation.Move, b++, Current(1), column: i - 1, row: 2).Result, Is.EqualTo(GomokuResult.Ok));
                Assert.That(Command(0, GomokuOperation.Move, a++, Current(0), column: i, row: 0).Result, Is.EqualTo(GomokuResult.Ok));
            }

            Assert.That(Current(1).Outcome, Is.EqualTo(GomokuOutcome.BlackVictory));
            Assert.That(Current(1).State, Is.EqualTo(GomokuRoomState.Finished));
            Assert.That(Command(1, GomokuOperation.Move, b++, Current(1), column: 8).Result, Is.EqualTo(GomokuResult.InvalidState));
            Assert.That(Current(1).Moves.Length, Is.EqualTo(9));
            Command(0, GomokuOperation.Leave, a++, Current(0));
            Assert.That(Current(1).Outcome, Is.EqualTo(GomokuOutcome.BlackVictory));
            Command(1, GomokuOperation.Leave, b++, Current(1));
            Assert.That(rooms.RoomCount, Is.Zero);
        }

        [Test]
        public void RevocationCannotRemoveNewBindingRoomAndOldBindingCannotSubmit()
        {
            var old = Player(101);
            var snapshot = Command(0, GomokuOperation.Create, 1).Snapshot!;
            old.Binding.Revoke(AccountSessionEndReason.Replaced);
            Player(101);
            var fresh = Command(1, GomokuOperation.Create, 1);
            Assert.That(fresh.Result, Is.EqualTo(GomokuResult.Ok));
            Assert.That(fresh.Snapshot!.RoomId, Is.Not.EqualTo(snapshot.RoomId));
            rooms.Detach(old.Binding.Id);
            Assert.That(Current(1).RoomId, Is.EqualTo(fresh.Snapshot.RoomId));
            Assert.Throws<ServerRequestException>(() => Command(0, GomokuOperation.Ready, 2, snapshot, ready: true));
            Assert.That(rooms.RoomCount, Is.EqualTo(1));
        }

        [Test]
        public void RevokedHostCannotAdmitNewGuestBeforeCloseCallbackRuns()
        {
            var host = Player();
            Player();
            string code = Command(0, GomokuOperation.Create, 1).Snapshot!.RoomCode;
            host.Binding.Revoke(AccountSessionEndReason.Replaced);
            Assert.That(Command(1, GomokuOperation.Join, 1, code: code).Result, Is.EqualTo(GomokuResult.RoomNotFound));
            Assert.That(rooms.RoomCount, Is.Zero);
        }

        [Test]
        public void ExpiredReceiptDoesNotPermitReplayAndQueryReturnsCurrentMembership()
        {
            Player();
            var created = Command(0, GomokuOperation.Create, 1);
            rooms.Sweep(NetworkClock.GetMilliseconds() + 120001);
            var result = rooms.Query(players[0].Binding, players[0].Connection, 1);
            Assert.That(result.CommandKnown, Is.False);
            Assert.That(result.Snapshot!.RoomId, Is.EqualTo(created.Snapshot!.RoomId));
            Assert.That(Command(0, GomokuOperation.Create, 1).Result, Is.EqualTo(GomokuResult.UnknownCommand));
            Assert.That(rooms.RoomCount, Is.EqualTo(1));
        }

        [Test]
        public void DisconnectAndExpirationReleaseRoomCapacity()
        {
            Player();
            Player();
            GomokuSnapshot snapshot = Command(0, GomokuOperation.Create, 1).Snapshot!;
            Command(1, GomokuOperation.Join, 1, code: snapshot.RoomCode);
            rooms.Detach(players[1].Binding.Id);
            Assert.That(Current(0).Members.Length, Is.EqualTo(1));
            rooms.Sweep(NetworkClock.GetMilliseconds() + 600001);
            Assert.That(rooms.RoomCount, Is.Zero);
            Assert.That(rooms.Query(players[0].Binding, players[0].Connection, 0).Snapshot, Is.Null);
        }

        [Test]
        public void PlayingDisconnectSetsOnlyFirstOutcomeAndStopAbortsActiveGames()
        {
            Player();
            Player();
            Command(0, GomokuOperation.Create, 1);
            Command(1, GomokuOperation.Join, 1, code: Current(0).RoomCode);
            Command(0, GomokuOperation.Ready, 2, Current(0), ready: true);
            Command(1, GomokuOperation.Ready, 2, Current(1), ready: true);
            Command(0, GomokuOperation.Start, 3, Current(0));
            rooms.Detach(players[1].Binding.Id);
            Assert.That(Current(0).Outcome, Is.EqualTo(GomokuOutcome.BlackVictory));
            rooms.StopAdmission();
            Assert.That(rooms.RoomCount, Is.Zero);
            Assert.That(Command(0, GomokuOperation.Create, 4).Result, Is.EqualTo(GomokuResult.Overloaded));
        }

        [Test]
        public async Task CapacityAndRateLimitsDoNotPreventLeaveOrCapacityReturn()
        {
            await rooms.DisposeAsync();
            rooms = new GomokuRooms(new GomokuOptions { MaxRooms = 1, MaxBindings = 2 });
            Player();
            Player();
            var first = Command(0, GomokuOperation.Create, 1);
            Assert.That(Command(1, GomokuOperation.Create, 1).Result, Is.EqualTo(GomokuResult.Overloaded));
            Assert.That(Command(0, GomokuOperation.Leave, 2, first.Snapshot).Result, Is.EqualTo(GomokuResult.Ok));
            Assert.That(Command(1, GomokuOperation.Create, 2).Result, Is.EqualTo(GomokuResult.Ok));
            Assert.That(Command(1, GomokuOperation.Create, 3).Result, Is.EqualTo(GomokuResult.AlreadyInRoom));
            Assert.That(Command(1, GomokuOperation.Create, 4).Result, Is.EqualTo(GomokuResult.RateLimited));
            Assert.That(Command(1, GomokuOperation.Leave, 5, Current(1)).Result, Is.EqualTo(GomokuResult.Ok));
            Assert.That(rooms.RoomCount, Is.Zero);
        }

        [Test]
        public void ActiveGameExpirationAbortsRatherThanAwardsAVictory()
        {
            Player();
            Player();
            Command(0, GomokuOperation.Create, 1);
            Command(1, GomokuOperation.Join, 1, code: Current(0).RoomCode);
            Command(0, GomokuOperation.Ready, 2, Current(0), ready: true);
            Command(1, GomokuOperation.Ready, 2, Current(1), ready: true);
            Command(0, GomokuOperation.Start, 3, Current(0));
            rooms.Sweep(NetworkClock.GetMilliseconds() + 3600001);
            Assert.That(Current(0).State, Is.EqualTo(GomokuRoomState.Finished));
            Assert.That(Current(0).Outcome, Is.EqualTo(GomokuOutcome.Aborted));
            Assert.That(Current(0).EndReason, Is.EqualTo(GomokuEndReason.Expired));
            rooms.Detach(players[1].Binding.Id);
            Assert.That(Current(0).Outcome, Is.EqualTo(GomokuOutcome.Aborted));
        }

        [Test]
        public void MaximumSnapshotFitsDeclaredWireBudget()
        {
            var snapshot = new GomokuSnapshot
            {
                RoomId = new string('a', 32), RoomCode = "01234567",
                Members = new[] { new GomokuMember(), new GomokuMember() },
                Moves = Enumerable.Range(0, 225).Select(i => new GomokuMoveData { Column = i % 15, Row = i / 15, Stone = 1 }).ToArray()
            };
            using var frame = memory.Allocate(32 + GameNetRoutes.GomokuRoomSnapshot.MaxBodyBytes);
            var writer = new BoundedFrameWriter(frame);
            GameNetRoutes.GomokuRoomSnapshot.Codec.Encode(writer, snapshot);
            writer.Complete(FrameKind.Push, GameNetRoutes.GomokuRoomSnapshot.Opcode, 0);
            Assert.That(frame.Length - 32, Is.LessThanOrEqualTo(4096));
            Assert.That(GameNetRoutes.GomokuRoomSnapshot.Codec.Decode(frame.Body).Moves.Length, Is.EqualTo(225));
        }
    }
}
