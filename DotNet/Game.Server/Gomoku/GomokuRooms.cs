using System.Security.Cryptography;
using System.Text;
using Game.Hotfix;
using Game.Hotfix.Network.Protocol;
using Game.Server.Login;
using GameFrame.Network;
using GameFrame.Server.Network;
using WireMove = Game.Hotfix.Network.Protocol.GomokuMoveData;

namespace Game.Server.Gomoku
{
    internal enum GomokuOperation
    {
        Create, Join, Ready, Start, Move, Leave
    }

    // One bounded, short commit gate owns rooms, account membership and command receipts.
    // No IO, encoding, await or external continuation runs while this gate is held.
    internal sealed class GomokuRooms : IAsyncDisposable
    {
        private sealed class Peer
        {
            internal required AccountSessions.Binding Binding;
            internal required ServerConnectionContext Connection;
            internal ulong HighWatermark;
            internal readonly Dictionary<ulong, Receipt> Receipts = new Dictionary<ulong, Receipt>();
            internal double Tokens = 3;
            internal long TokenTime;
        }

        private sealed record Receipt(string Fingerprint, GomokuResult Result, long Deadline);
        private sealed class Member
        {
            internal required Peer Peer;
            internal ulong Id;
            internal ulong Admission;
            internal bool Ready;
            internal bool Host;
        }

        private sealed class Room
        {
            internal string Id = Guid.NewGuid().ToString("N");
            internal required string Code;
            internal readonly List<Member> Members = new List<Member>(2);
            internal readonly GomokuGame Game = new GomokuGame(15);
            internal ulong Version = 1;
            internal GomokuRoomState State;
            internal GomokuOutcome Outcome;
            internal GomokuEndReason Reason;
            internal long Deadline;
        }

        private sealed record Output(ServerConnectionContext Connection, GomokuSnapshot Snapshot);
        private readonly object gate = new object();
        private readonly GomokuOptions options;
        private readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        private readonly Dictionary<string, Room> codes = new Dictionary<string, Room>();
        private readonly Dictionary<ulong, Room> membership = new Dictionary<ulong, Room>();
        private readonly Dictionary<long, Peer> peers = new Dictionary<long, Peer>();
        // A full snapshot supersedes earlier pending snapshots for the same binding.
        // At most MaxBindings pending + MaxBindings draining outputs, each <= 4096 wire bytes.
        private readonly Dictionary<long, Output> outputs = new Dictionary<long, Output>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Task driver;
        private Task? disposal;
        private Exception? fault;
        private bool accepting = true;
        private ulong nextMember;

        internal GomokuRooms(GomokuOptions? options = null)
        {
            this.options = options ?? new GomokuOptions();
            this.options.Validate();
            driver = DriveAsync();
        }

        internal int RoomCount
        {
            get
            {
                lock (gate)
                {
                    return rooms.Count;
                }
            }
        }

        internal GomokuResponse Execute(AccountSessions.Binding binding, ServerConnectionContext connection,
            GomokuOperation operation, GomokuCommand command)
        {
            lock (gate)
            {
                lock (binding.CommitGate)
                {
                    RequireAuthorized(binding, connection);
                    if (fault != null)
                    {
                        throw new InvalidOperationException("Gomoku room driver failed.", fault);
                    }

                    long now = NetworkClock.GetMilliseconds();
                    if (!peers.TryGetValue(binding.Id, out Peer? peer))
                    {
                        if (!accepting || peers.Count >= options.MaxBindings)
                        {
                            return Response(binding, command.CommandId, GomokuResult.Overloaded, false);
                        }

                        peer = new Peer { Binding = binding, Connection = connection, TokenTime = now };
                        peers.Add(binding.Id, peer);
                    }

                    ExpireReceipts(peer, now);
                    string fingerprint = Fingerprint(operation, command);
                    if (peer.Receipts.TryGetValue(command.CommandId, out Receipt? previous))
                    {
                        return Response(binding, command.CommandId,
                            previous.Fingerprint == fingerprint ? previous.Result : GomokuResult.InvalidCommand, true);
                    }

                    if (command.CommandId == 0 || command.CommandId <= peer.HighWatermark)
                    {
                        return Response(binding, command.CommandId, GomokuResult.UnknownCommand, false);
                    }

                    // The high watermark survives receipt eviction for the entire binding.
                    peer.HighWatermark = command.CommandId;
                    GomokuResult result;
                    if (!accepting && operation != GomokuOperation.Leave)
                    {
                        result = GomokuResult.Overloaded;
                    }
                    else if ((operation == GomokuOperation.Create || operation == GomokuOperation.Join) && !Admit(peer, now))
                    {
                        result = GomokuResult.RateLimited;
                    }
                    else
                    {
                        RemoveStaleMembership(binding.Identity.AccountId);
                        result = Apply(peer, operation, command, now);
                    }

                    if (peer.Receipts.Count == 32)
                    {
                        peer.Receipts.Remove(peer.Receipts.Keys.Min());
                    }

                    peer.Receipts.Add(command.CommandId, new Receipt(fingerprint, result,
                        checked(now + (long)options.CommandLifetime.TotalMilliseconds)));
                    return Response(binding, command.CommandId, result, true);
                }
            }
        }

        internal GomokuResponse Query(AccountSessions.Binding binding, ServerConnectionContext connection, ulong commandId)
        {
            lock (gate)
            {
                lock (binding.CommitGate)
                {
                    RequireAuthorized(binding, connection);
                    GomokuResult result = GomokuResult.UnknownCommand;
                    bool known = commandId == 0;
                    if (peers.TryGetValue(binding.Id, out Peer? peer))
                    {
                        ExpireReceipts(peer, NetworkClock.GetMilliseconds());
                        if (peer.Receipts.TryGetValue(commandId, out Receipt? receipt))
                        {
                            known = true;
                            result = receipt.Result;
                        }
                    }

                    return Response(binding, commandId, known && commandId == 0 ? GomokuResult.Ok : result, known);
                }
            }
        }

        private static void RequireAuthorized(AccountSessions.Binding binding, ServerConnectionContext connection)
        {
            connection.Cancellation.ThrowIfCancellationRequested();
            if (binding.CloseReason != null)
            {
                throw new ServerRequestException(2, 5);
            }
        }

        private GomokuResult Apply(Peer peer, GomokuOperation operation, GomokuCommand command, long now)
        {
            membership.TryGetValue(peer.Binding.Identity.AccountId, out Room? target);
            if (operation == GomokuOperation.Join)
            {
                codes.TryGetValue(command.RoomCode, out target);
            }

            Member? other = target?.Members.Find(m => m.Peer.Binding.Id != peer.Binding.Id);
            if (other == null)
            {
                return ApplyCore(peer, operation, command, now);
            }

            // There are only two seats. The collection gate serializes acquisition of
            // both binding gates; revocation never acquires the collection gate.
            lock (other.Peer.Binding.CommitGate)
            {
                if (other.Peer.Binding.CloseReason != null || other.Peer.Connection.Cancellation.IsCancellationRequested)
                {
                    RemoveMember(target!, other, GomokuEndReason.Disconnected);
                }

                return ApplyCore(peer, operation, command, now);
            }
        }

        private GomokuResult ApplyCore(Peer peer, GomokuOperation operation, GomokuCommand command, long now)
        {
            RequireAuthorized(peer.Binding, peer.Connection);
            ulong account = peer.Binding.Identity.AccountId;
            membership.TryGetValue(account, out Room? room);
            if (operation == GomokuOperation.Create || operation == GomokuOperation.Join)
            {
                if (room != null)
                {
                    return GomokuResult.AlreadyInRoom;
                }

                if (operation == GomokuOperation.Create)
                {
                    if (rooms.Count >= options.MaxRooms)
                    {
                        return GomokuResult.Overloaded;
                    }

                    string? code = null;
                    for (int i = 0; i < 16; i++)
                    {
                        string candidate = RandomNumberGenerator.GetInt32(100000000).ToString("D8");
                        if (!codes.ContainsKey(candidate))
                        {
                            code = candidate;
                            break;
                        }
                    }

                    if (code == null)
                    {
                        return GomokuResult.Overloaded;
                    }

                    room = new Room { Code = code, Deadline = checked(now + (long)options.WaitingLifetime.TotalMilliseconds) };
                    rooms.Add(room.Id, room);
                    codes.Add(room.Code, room);
                }
                else
                {
                    if (command.RoomCode.Length != 8 || command.RoomCode.Any(c => c < '0' || c > '9') ||
                        !codes.TryGetValue(command.RoomCode, out room))
                    {
                        return GomokuResult.RoomNotFound;
                    }

                    if (room.State != GomokuRoomState.Waiting || now >= room.Deadline)
                    {
                        return GomokuResult.InvalidState;
                    }

                    if (room.Members.Count == 2)
                    {
                        return GomokuResult.RoomFull;
                    }
                }

                var member = new Member
                {
                    Peer = peer, Id = checked(++nextMember), Admission = command.CommandId,
                    Host = operation == GomokuOperation.Create
                };
                room.Members.Add(member);
                membership.Add(account, room);
                foreach (Member current in room.Members)
                {
                    current.Ready = false;
                }

                room.Version++;
                Publish(room);
                return GomokuResult.Ok;
            }

            Member? own = room?.Members.Find(m => m.Peer.Binding.Id == peer.Binding.Id);
            if (room == null || own == null || room.Id != command.RoomId)
            {
                return GomokuResult.RoomNotFound;
            }

            // Leave never needs a current board version; it must remain possible under load.
            if (operation == GomokuOperation.Leave)
            {
                RemoveMember(room, own, GomokuEndReason.PlayerLeft);
                return GomokuResult.Ok;
            }

            if (room.Version != command.ExpectedVersion)
            {
                return GomokuResult.StaleVersion;
            }

            if (now >= room.Deadline)
            {
                return GomokuResult.InvalidState;
            }

            switch (operation)
            {
                case GomokuOperation.Ready:
                    if (room.State != GomokuRoomState.Waiting)
                    {
                        return GomokuResult.InvalidState;
                    }

                    own.Ready = command.Ready;
                    break;
                case GomokuOperation.Start:
                    if (room.State != GomokuRoomState.Waiting)
                    {
                        return GomokuResult.InvalidState;
                    }

                    if (!own.Host)
                    {
                        return GomokuResult.NotHost;
                    }

                    if (room.Members.Count != 2 || room.Members.Any(m => !m.Ready || m.Peer.Binding.CloseReason != null))
                    {
                        return GomokuResult.NotReady;
                    }

                    room.State = GomokuRoomState.Playing;
                    room.Deadline = checked(now + (long)options.GameLifetime.TotalMilliseconds);
                    break;
                case GomokuOperation.Move:
                    if (room.State != GomokuRoomState.Playing)
                    {
                        return GomokuResult.InvalidState;
                    }

                    GomokuStone stone = own.Host ? GomokuStone.Black : GomokuStone.White;
                    if (room.Game.PlaceMove(command.Column, command.Row, stone) != GomokuMoveResult.Accepted)
                    {
                        return GomokuResult.InvalidMove;
                    }

                    if (room.Game.State != GomokuGameState.Playing)
                    {
                        Finish(room, room.Game.State switch
                        {
                            GomokuGameState.BlackWon => GomokuOutcome.BlackVictory,
                            GomokuGameState.WhiteWon => GomokuOutcome.WhiteVictory,
                            _ => GomokuOutcome.Draw
                        }, room.Game.State == GomokuGameState.Draw ? GomokuEndReason.BoardFull : GomokuEndReason.FiveInRow);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            room.Version++;
            Publish(room);
            return GomokuResult.Ok;
        }

        internal void Detach(long bindingId)
        {
            lock (gate)
            {
                if (!peers.Remove(bindingId, out Peer? peer))
                {
                    return;
                }

                if (membership.TryGetValue(peer.Binding.Identity.AccountId, out Room? room))
                {
                    Member? member = room.Members.Find(m => m.Peer.Binding.Id == bindingId);
                    if (member != null)
                    {
                        RemoveMember(room, member, GomokuEndReason.Disconnected);
                    }
                }

                outputs.Remove(bindingId);
            }
        }

        private void RemoveStaleMembership(ulong accountId)
        {
            if (membership.TryGetValue(accountId, out Room? room))
            {
                Member? old = room.Members.Find(m => m.Peer.Binding.Identity.AccountId == accountId &&
                    m.Peer.Binding.CloseReason != null);
                if (old != null)
                {
                    RemoveMember(room, old, GomokuEndReason.Disconnected);
                }
            }
        }

        private void RemoveMember(Room room, Member member, GomokuEndReason reason)
        {
            if (!room.Members.Contains(member))
            {
                return;
            }

            if (room.State == GomokuRoomState.Waiting && member.Host)
            {
                CloseRoom(room, reason);
                return;
            }

            if (room.State == GomokuRoomState.Playing)
            {
                Finish(room, member.Host ? GomokuOutcome.WhiteVictory : GomokuOutcome.BlackVictory, reason);
            }

            room.Version++;
            Queue(member, Snapshot(room, member, closed: true));
            membership.Remove(member.Peer.Binding.Identity.AccountId);
            room.Members.Remove(member);
            foreach (Member current in room.Members)
            {
                current.Ready = false;
            }

            if (room.Members.Count == 0)
            {
                CloseRoom(room, reason);
            }
            else
            {
                Publish(room);
            }
        }

        private void Finish(Room room, GomokuOutcome outcome, GomokuEndReason reason)
        {
            room.State = GomokuRoomState.Finished;
            room.Outcome = outcome;
            room.Reason = reason;
            room.Deadline = checked(NetworkClock.GetMilliseconds() + (long)options.FinishedLifetime.TotalMilliseconds);
        }

        private void CloseRoom(Room room, GomokuEndReason reason)
        {
            if (room.State != GomokuRoomState.Finished)
            {
                room.Reason = reason;
            }

            room.State = GomokuRoomState.Closed;
            if (room.Outcome == GomokuOutcome.NoOutcome)
            {
                room.Outcome = GomokuOutcome.Aborted;
            }

            room.Version++;
            Publish(room);
            foreach (Member member in room.Members)
            {
                membership.Remove(member.Peer.Binding.Identity.AccountId);
            }

            room.Members.Clear();
            rooms.Remove(room.Id);
            codes.Remove(room.Code);
        }

        private GomokuResponse Response(AccountSessions.Binding binding, ulong id, GomokuResult result, bool known)
        {
            GomokuSnapshot? snapshot = null;
            if (membership.TryGetValue(binding.Identity.AccountId, out Room? room))
            {
                Member? member = room.Members.Find(m => m.Peer.Binding.Id == binding.Id);
                if (member != null)
                {
                    snapshot = Snapshot(room, member);
                }
            }

            return new GomokuResponse { Result = result, CommandId = id, CommandKnown = known, Snapshot = snapshot };
        }

        private static GomokuSnapshot Snapshot(Room room, Member recipient, bool closed = false) => new GomokuSnapshot
        {
            RoomId = room.Id, RoomCode = room.Code, Version = room.Version,
            State = closed ? GomokuRoomState.Closed : room.State, Outcome = room.Outcome, EndReason = room.Reason,
            RecipientAccountId = recipient.Peer.Binding.Identity.AccountId,
            RecipientAuthEpoch = recipient.Peer.Binding.Identity.AuthEpoch,
            RecipientMemberId = recipient.Id, AdmissionCommandId = recipient.Admission,
            Members = room.Members.Select(m => new GomokuMember
            {
                AccountId = m.Peer.Binding.Identity.AccountId, MemberId = m.Id, Ready = m.Ready, IsHost = m.Host
            }).ToArray(),
            Moves = room.Game.Moves.Select(m => new WireMove { Column = m.Column, Row = m.Row, Stone = (int)m.Stone }).ToArray()
        };

        private void Publish(Room room)
        {
            foreach (Member member in room.Members)
            {
                Queue(member, Snapshot(room, member));
            }
        }

        private void Queue(Member member, GomokuSnapshot snapshot)
        {
            // A detached binding cannot accumulate output. Existing peer slots reserve capacity.
            if (peers.ContainsKey(member.Peer.Binding.Id))
            {
                outputs[member.Peer.Binding.Id] = new Output(member.Peer.Connection, snapshot);
            }
        }

        private static string Fingerprint(GomokuOperation operation, GomokuCommand command) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{(int)operation}|{command.RoomId.Length}:{command.RoomId}|{command.ExpectedVersion}|{command.RoomCode.Length}:{command.RoomCode}|{command.Ready}|{command.Column}|{command.Row}")));

        private static bool Admit(Peer peer, long now)
        {
            peer.Tokens = Math.Min(3, peer.Tokens + (now - peer.TokenTime) / 1000.0);
            peer.TokenTime = now;
            if (peer.Tokens < 1)
            {
                return false;
            }

            peer.Tokens--;
            return true;
        }

        private static void ExpireReceipts(Peer peer, long now)
        {
            foreach (ulong id in peer.Receipts.Where(p => now >= p.Value.Deadline).Select(p => p.Key).ToArray())
            {
                peer.Receipts.Remove(id);
            }
        }

        internal void Sweep(long now)
        {
            lock (gate)
            {
                foreach (Room room in rooms.Values.ToArray())
                {
                    foreach (Member member in room.Members.ToArray())
                    {
                        if (member.Peer.Binding.CloseReason != null || member.Peer.Connection.Cancellation.IsCancellationRequested)
                        {
                            RemoveMember(room, member, GomokuEndReason.Disconnected);
                        }
                    }

                    if (!rooms.ContainsKey(room.Id) || now < room.Deadline)
                    {
                        continue;
                    }

                    if (room.State == GomokuRoomState.Playing)
                    {
                        Finish(room, GomokuOutcome.Aborted, GomokuEndReason.Expired);
                        room.Version++;
                        Publish(room);
                    }
                    else
                    {
                        CloseRoom(room, GomokuEndReason.Expired);
                    }
                }

                foreach (Peer peer in peers.Values)
                {
                    ExpireReceipts(peer, now);
                }
            }
        }

        private async Task DriveAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    Sweep(NetworkClock.GetMilliseconds());
                    Output[] batch;
                    lock (gate)
                    {
                        batch = outputs.Values.ToArray();
                        outputs.Clear();
                    }

                    foreach (Output output in batch)
                    {
                        try
                        {
                            await output.Connection.PushAsync(output.Snapshot.State == GomokuRoomState.Closed
                                ? Game.Hotfix.Network.Generated.GameNetRoutes.GomokuRoomClosed
                                : Game.Hotfix.Network.Generated.GameNetRoutes.GomokuRoomSnapshot, output.Snapshot).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is NetworkException || error is OperationCanceledException)
                        {
                            // A slow/closed recipient cannot lose a reliable state update and keep playing.
                            output.Connection.RequestClose(1);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                ServerConnectionContext[] connections;
                lock (gate)
                {
                    fault = error;
                    accepting = false;
                    connections = peers.Values.Select(p => p.Connection).ToArray();
                    foreach (Room room in rooms.Values.ToArray())
                    {
                        if (room.State != GomokuRoomState.Finished)
                        {
                            room.Outcome = GomokuOutcome.Aborted;
                        }

                        CloseRoom(room, GomokuEndReason.ServiceStopped);
                    }
                }

                foreach (ServerConnectionContext connection in connections)
                {
                    connection.RequestClose(1);
                }

                throw;
            }
        }

        internal void StopAdmission()
        {
            lock (gate)
            {
                accepting = false;
                foreach (Room room in rooms.Values.ToArray())
                {
                    if (room.State != GomokuRoomState.Finished)
                    {
                        room.Outcome = GomokuOutcome.Aborted;
                    }

                    CloseRoom(room, GomokuEndReason.ServiceStopped);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposal == null)
                {
                    accepting = false;
                    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    disposal = completed.Task;
                    _ = StopCoreAsync(completed);
                }

                return new ValueTask(disposal);
            }
        }

        private async Task StopCoreAsync(TaskCompletionSource<bool> completed)
        {
            // Start outside the commit gate, even if called by DisposeAsync while it owns the gate.
            await Task.Yield();
            Exception? failure = null;
            try
            {
                lifetime.Cancel();
                await driver.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                lock (gate)
                {
                    rooms.Clear();
                    codes.Clear();
                    membership.Clear();
                    peers.Clear();
                    outputs.Clear();
                }

                lifetime.Dispose();
            }

            if (failure == null)
            {
                completed.TrySetResult(true);
            }
            else
            {
                completed.TrySetException(failure);
            }
        }
    }
}
