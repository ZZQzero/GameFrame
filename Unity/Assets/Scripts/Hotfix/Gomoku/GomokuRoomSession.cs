using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using GameFrame.Network;

namespace Game.Hotfix
{
    /// <summary>主线程房间状态所有者；借用账号 WSS，面板关闭不结束房间。</summary>
    public sealed class GomokuRoomSession : IAsyncDisposable
    {
        readonly GameLoginSession login;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        IDisposable snapshots;
        IDisposable closed;
        Task operation;
        Task disposal;
        ulong nextCommand;
        ulong uncertainCommand;
        ulong admission;
        bool baselinePending;
        GomokuSnapshot earlySnapshot;
        string closedRoom;
        ulong closedMember;
        ulong closedVersion;

        public event Action Changed;
        public GomokuRoomView Snapshot { get; private set; }
        public IGomokuGameView Game { get; private set; }
        public bool IsBusy => operation != null;
        public bool NeedsQuery { get; private set; }
        public bool LastUpdateWasBaseline { get; private set; }
        public string Notice { get; private set; } = string.Empty;
        public ulong AccountId => login.CurrentUser?.AccountId ?? 0;

        public GomokuRoomSession(GameLoginSession login)
        {
            this.login = login ?? throw new ArgumentNullException(nameof(login));
            login.AuthenticationLost += AuthenticationLost;
        }

        public void Activate()
        {
            RequireMainThread();
            if (disposal != null)
            {
                throw new ObjectDisposedException(nameof(GomokuRoomSession));
            }

            if (snapshots == null)
            {
                NetworkClient client = login.LobbyClient;
                snapshots = client.Subscribe(GameNetRoutes.GomokuRoomSnapshot, OnSnapshot);
                try
                {
                    closed = client.Subscribe(GameNetRoutes.GomokuRoomClosed, OnSnapshot);
                }
                catch
                {
                    snapshots.Dispose();
                    snapshots = null;
                    throw;
                }
            }
        }

        public UniTask<GomokuResult> CreateAsync(CancellationToken token = default) =>
            CommandAsync(GameNetRoutes.CreateGomokuRoom, new GomokuCommand(), true, token);
        public UniTask<GomokuResult> JoinAsync(string code, CancellationToken token = default)
        {
            if (code == null || code.Length != 8)
            {
                throw new ArgumentException("房间号必须是 8 位数字。", nameof(code));
            }

            foreach (char digit in code)
            {
                if (digit < '0' || digit > '9')
                {
                    throw new ArgumentException("房间号必须是 8 位数字。", nameof(code));
                }
            }

            return CommandAsync(GameNetRoutes.JoinGomokuRoom, new GomokuCommand { RoomCode = code }, true, token);
        }

        public UniTask<GomokuResult> ReadyAsync(bool ready, CancellationToken token = default) =>
            CommandAsync(GameNetRoutes.SetGomokuReady, RoomCommand(ready), false, token);
        public UniTask<GomokuResult> StartAsync(CancellationToken token = default) =>
            CommandAsync(GameNetRoutes.StartGomokuGame, RoomCommand(), false, token);
        public UniTask<GomokuResult> MoveAsync(int column, int row, CancellationToken token = default)
        {
            GomokuCommand command = RoomCommand();
            command.Column = column;
            command.Row = row;
            return CommandAsync(GameNetRoutes.PlaceGomokuMove, command, false, token);
        }

        public UniTask<GomokuResult> LeaveAsync(CancellationToken token = default) =>
            CommandAsync(GameNetRoutes.LeaveGomokuRoom, RoomCommand(), false, token);

        GomokuCommand RoomCommand(bool ready = false)
        {
            RequireMainThread();
            if (Snapshot == null)
            {
                throw new InvalidOperationException("当前未加入房间。");
            }

            return new GomokuCommand { RoomId = Snapshot.RoomId, ExpectedVersion = Snapshot.Version, Ready = ready };
        }

        async UniTask<GomokuResult> CommandAsync(NetRpc<GomokuCommand, GomokuResponse> route,
            GomokuCommand command, bool entering, CancellationToken token)
        {
            Activate();
            if (NeedsQuery)
            {
                throw new InvalidOperationException("上次结果尚未确认，请先查询状态。");
            }

            GameUser user = login.CurrentUser;
            if (entering && Snapshot != null)
            {
                throw new InvalidOperationException("请先退出当前房间。");
            }

            token.ThrowIfCancellationRequested();
            command.CommandId = checked(++nextCommand);
            TaskCompletionSource<bool> completed = BeginOperation();
            uncertainCommand = command.CommandId;
            Notice = string.Empty;
            if (entering)
            {
                BeginBaseline(command.CommandId);
            }

            try
            {
                Changed?.Invoke();
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                GomokuResponse response = await login.LobbyClient.RequestAsync(route, command, TimeSpan.FromSeconds(5), cancellation.Token);
                if (!ReferenceEquals(user, login.CurrentUser) || disposal != null)
                {
                    return response.Result;
                }

                if (response.CommandId != command.CommandId)
                {
                    throw new NetworkProtocolException("房间响应命令标识不匹配。");
                }

                if (entering && response.Result == GomokuResult.Ok &&
                    (response.Snapshot == null || response.Snapshot.AdmissionCommandId != command.CommandId))
                {
                    throw new NetworkProtocolException("入房响应未提供匹配的成员基线。");
                }

                if (response.Snapshot != null)
                {
                    if (entering)
                    {
                        ApplyBaseline(response.Snapshot, user);
                    }
                    else
                    {
                        Apply(response.Snapshot, user, baseline: false);
                    }
                }
                else if (route == GameNetRoutes.LeaveGomokuRoom && response.Result == GomokuResult.Ok)
                {
                    ClearRoom("已退出房间");
                }

                if (response.Result != GomokuResult.Ok)
                {
                    Notice = ResultText(response.Result);
                }

                return response.Result;
            }
            catch
            {
                if (ReferenceEquals(user, login.CurrentUser) && disposal == null)
                {
                    NeedsQuery = true;
                    Notice = "请求结果未确认，请点击查询状态";
                }

                throw;
            }
            finally
            {
                admission = 0;
                baselinePending = false;
                earlySnapshot = null;
                operation = null;
                completed.TrySetResult(true);
                Changed?.Invoke();
            }
        }

        public async UniTask QueryAsync(CancellationToken token = default)
        {
            Activate();
            GameUser user = login.CurrentUser;
            TaskCompletionSource<bool> completed = BeginOperation();
            BeginBaseline(0);
            try
            {
                Changed?.Invoke();
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                GomokuResponse response = await login.LobbyClient.RequestAsync(GameNetRoutes.QueryMyGomokuRoom,
                    new GomokuQuery { CommandId = uncertainCommand }, TimeSpan.FromSeconds(5), cancellation.Token);
                if (!ReferenceEquals(user, login.CurrentUser) || disposal != null)
                {
                    return;
                }

                if (response.CommandId != uncertainCommand)
                {
                    throw new NetworkProtocolException("查询响应命令标识不匹配。");
                }

                if (response.Snapshot == null)
                {
                    ClearRoom("当前没有房间");
                }
                else
                {
                    ApplyBaseline(response.Snapshot, user);
                }

                NeedsQuery = false;
                Notice = response.CommandKnown ? ResultText(response.Result) : "已同步当前状态；原命令结果未知，请勿重复发送";
            }
            finally
            {
                baselinePending = false;
                earlySnapshot = null;
                operation = null;
                completed.TrySetResult(true);
                Changed?.Invoke();
            }
        }

        TaskCompletionSource<bool> BeginOperation()
        {
            if (operation != null)
            {
                throw new InvalidOperationException("房间操作正在执行。");
            }

            var completed = new TaskCompletionSource<bool>();
            operation = completed.Task;
            return completed;
        }

        void OnSnapshot(GomokuSnapshot snapshot)
        {
            GameUser user = login.CurrentUser;
            if (user == null || disposal != null || snapshot.RecipientAccountId != user.AccountId ||
                snapshot.RecipientAuthEpoch != user.AuthEpoch)
            {
                return;
            }

            if (Snapshot == null && baselinePending && (admission == 0 || snapshot.AdmissionCommandId == admission))
            {
                if (earlySnapshot == null || snapshot.AdmissionCommandId > earlySnapshot.AdmissionCommandId ||
                    (snapshot.RoomId == earlySnapshot.RoomId && snapshot.RecipientMemberId == earlySnapshot.RecipientMemberId &&
                        snapshot.Version > earlySnapshot.Version))
                {
                    earlySnapshot = snapshot;
                }

                return;
            }

            if (Snapshot != null && snapshot.RoomId == Snapshot.RoomId && snapshot.RecipientMemberId == Snapshot.RecipientMemberId)
            {
                Apply(snapshot, user, baseline: false);
            }
        }

        void BeginBaseline(ulong commandId)
        {
            admission = commandId;
            baselinePending = true;
            earlySnapshot = null;
        }

        void ApplyBaseline(GomokuSnapshot snapshot, GameUser user)
        {
            // One in-flight baseline owns at most one candidate. Only the response's
            // membership can promote it; an earlier response must not revive a closed room.
            GomokuSnapshot candidate = earlySnapshot;
            if (candidate != null && candidate.RoomId == snapshot.RoomId &&
                candidate.RecipientMemberId == snapshot.RecipientMemberId && candidate.Version > snapshot.Version)
            {
                snapshot = candidate;
            }

            earlySnapshot = null;
            Apply(snapshot, user, baseline: true);
        }

        void Apply(GomokuSnapshot snapshot, GameUser user, bool baseline)
        {
            if (snapshot.RecipientAccountId != user.AccountId || snapshot.RecipientAuthEpoch != user.AuthEpoch)
            {
                throw new NetworkProtocolException("房间快照不属于当前账号。");
            }

            // A later Close push may arrive before the earlier RPC response.
            if (snapshot.RoomId == closedRoom && snapshot.RecipientMemberId == closedMember && snapshot.Version <= closedVersion)
            {
                return;
            }

            if (Snapshot != null)
            {
                bool same = snapshot.RoomId == Snapshot.RoomId && snapshot.RecipientMemberId == Snapshot.RecipientMemberId;
                if (same && snapshot.Version <= Snapshot.Version)
                {
                    return;
                }

                if (!same && !baseline)
                {
                    return;
                }
            }

            if (snapshot.RoomId.Length != 32 || snapshot.RoomCode.Length != 8 || snapshot.Version == 0 || snapshot.RecipientMemberId == 0)
            {
                throw new NetworkProtocolException("无效的房间标识或版本。");
            }

            if (snapshot.State == GomokuRoomState.Closed)
            {
                closedRoom = snapshot.RoomId;
                closedMember = snapshot.RecipientMemberId;
                closedVersion = snapshot.Version;
                ClearRoom("房间已关闭：" + snapshot.EndReason);
                Changed?.Invoke();
                return;
            }

            bool own = false;
            int hosts = 0;
            for (int i = 0; i < snapshot.Members.Length; i++)
            {
                GomokuMember member = snapshot.Members[i];
                if (member.AccountId == 0 || member.MemberId == 0 ||
                    (i > 0 && (member.AccountId == snapshot.Members[0].AccountId || member.MemberId == snapshot.Members[0].MemberId)))
                {
                    throw new NetworkProtocolException("无效或重复的房间成员。");
                }

                hosts += member.IsHost ? 1 : 0;
                own |= member.MemberId == snapshot.RecipientMemberId && member.AccountId == user.AccountId;
            }

            if (!own || snapshot.Members.Length == 0 || snapshot.Members.Length > 2 ||
                hosts > 1 || (snapshot.State != GomokuRoomState.Finished && hosts != 1) ||
                (snapshot.State == GomokuRoomState.Playing && snapshot.Members.Length != 2))
            {
                throw new NetworkProtocolException("无效的房间成员资格。");
            }

            var game = new GomokuGame(15);
            foreach (GomokuMoveData move in snapshot.Moves)
            {
                if ((move.Stone != 1 && move.Stone != 2) ||
                    game.PlaceMove(move.Column, move.Row, (GomokuStone)move.Stone) != GomokuMoveResult.Accepted)
                {
                    throw new NetworkProtocolException("无效的服务端棋局历史。");
                }
            }

            if (snapshot.State == GomokuRoomState.Waiting && snapshot.Moves.Length != 0)
            {
                throw new NetworkProtocolException("等待房间包含了棋局历史。");
            }

            if ((snapshot.State != GomokuRoomState.Finished && snapshot.Outcome != GomokuOutcome.NoOutcome) ||
                (snapshot.State == GomokuRoomState.Playing && game.State != GomokuGameState.Playing) ||
                (snapshot.State == GomokuRoomState.Finished && snapshot.Outcome == GomokuOutcome.NoOutcome) ||
                (snapshot.EndReason == GomokuEndReason.FiveInRow &&
                    snapshot.Outcome != (game.State == GomokuGameState.BlackWon ? GomokuOutcome.BlackVictory :
                        game.State == GomokuGameState.WhiteWon ? GomokuOutcome.WhiteVictory : GomokuOutcome.NoOutcome)) ||
                (snapshot.EndReason == GomokuEndReason.BoardFull &&
                    (snapshot.Outcome != GomokuOutcome.Draw || game.State != GomokuGameState.Draw)))
            {
                throw new NetworkProtocolException("棋局状态与权威结果不一致。");
            }

            LastUpdateWasBaseline = baseline;
            Snapshot = new GomokuRoomView(snapshot);
            Game = game;
            Changed?.Invoke();
        }

        void ClearRoom(string notice)
        {
            Snapshot = null;
            Game = null;
            Notice = notice;
        }

        void AuthenticationLost()
        {
            snapshots?.Dispose();
            closed?.Dispose();
            snapshots = null;
            closed = null;
            admission = 0;
            baselinePending = false;
            earlySnapshot = null;
            NeedsQuery = false;
            closedRoom = null;
            closedMember = 0;
            closedVersion = 0;
            ClearRoom("账号会话已结束");
            Changed?.Invoke();
        }

        public static string ResultText(GomokuResult result)
        {
            switch (result)
            {
                case GomokuResult.Ok:
                    return string.Empty;
                case GomokuResult.RoomNotFound:
                    return "房间不存在或已关闭";
                case GomokuResult.RoomFull:
                    return "房间已满";
                case GomokuResult.AlreadyInRoom:
                    return "请先退出当前房间";
                case GomokuResult.NotHost:
                    return "只有房主可以开始";
                case GomokuResult.NotReady:
                    return "等待双方准备";
                case GomokuResult.StaleVersion:
                    return "状态已更新，请按当前状态操作";
                case GomokuResult.InvalidMove:
                    return "当前不能在这里落子";
                case GomokuResult.RateLimited:
                    return "操作过于频繁，请稍后操作";
                case GomokuResult.Overloaded:
                    return "房间服务繁忙或正在停止";
                case GomokuResult.UnknownCommand:
                    return "原命令结果未知，请查询当前状态";
                default:
                    return "操作被拒绝：" + result;
            }
        }

        public ValueTask DisposeAsync()
        {
            RequireMainThread();
            if (disposal == null)
            {
                var completed = new TaskCompletionSource<bool>();
                disposal = completed.Task;
                _ = DisposeCoreAsync(completed);
            }

            return new ValueTask(disposal);
        }

        async Task DisposeCoreAsync(TaskCompletionSource<bool> completed)
        {
            Exception failure = null;
            void Cleanup(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    if (failure == null)
                    {
                        failure = error;
                    }
                    else
                    {
                        UnityEngine.Debug.LogException(error);
                    }
                }
            }

            Cleanup(lifetime.Cancel);
            try
            {
                if (operation != null)
                {
                    await operation;
                }
            }
            catch (Exception error)
            {
                if (failure == null)
                {
                    failure = error;
                }
                else
                {
                    UnityEngine.Debug.LogException(error);
                }
            }

            Cleanup(() => snapshots?.Dispose());
            Cleanup(() => closed?.Dispose());
            login.AuthenticationLost -= AuthenticationLost;
            ClearRoom(string.Empty);
            Changed = null;
            Cleanup(lifetime.Dispose);
            if (failure == null)
            {
                completed.TrySetResult(true);
            }
            else
            {
                completed.TrySetException(failure);
            }
        }

        static void RequireMainThread()
        {
            if (!PlayerLoopHelper.IsMainThread)
            {
                throw new InvalidOperationException("房间会话必须在 Unity 主线程使用。");
            }
        }
    }
}
