using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Protocol.Gomoku;
using GameFrame.Audio;
using GameFrame.UI;

namespace Game.Hotfix
{
    public partial class GomokuOnlinePanel : UIPanel<GomokuRoomSession>
    {
        GomokuRoomSession session;
        int renderedMoves;
        IGomokuGameView renderedGame;
        bool paused;

        protected override void OnCreate()
        {
            boardArea.IntersectionClicked += Move;
            queryButton.onClick.AddListener(Query);
            exitButton.onClick.AddListener(Exit);
            LifetimeScope.Register(() => boardArea.IntersectionClicked -= Move);
            LifetimeScope.Register(() => queryButton.onClick.RemoveListener(Query));
            LifetimeScope.Register(() => exitButton.onClick.RemoveListener(Exit));
        }

        protected override void OnOpen(GomokuRoomSession args)
        {
            session = args ?? throw new ArgumentNullException(nameof(args));
            paused = false;
            renderedGame = null;
            renderedMoves = session.Game?.Moves.Count ?? 0;
            boardArea.Grid.SetPointCount(15);
            session.Changed += Refresh;
            GomokuRoomSession current = session;
            OpenScope.Register(() => current.Changed -= Refresh);
            OpenScope.Register(() => boardArea.Interactable = false);
            Refresh();
        }

        protected override void OnPause()
        {
            paused = true;
            boardArea.Interactable = false;
        }

        protected override void OnResume()
        {
            paused = false;
            renderedMoves = session.Game?.Moves.Count ?? 0;
            Refresh();
        }

        void Move(int column, int row) => RunAsync(async (current, token) =>
        {
            await current.MoveAsync(column, row, token);
        }).Forget();
        void Query() => RunAsync((current, token) => current.QueryAsync(token)).Forget();
        void Exit() => RunAsync(async (current, token) =>
        {
            GomokuResult result = await current.LeaveAsync(token);
            if (result == GomokuResult.Ok && !token.IsCancellationRequested)
            {
                CloseSelf();
            }
        }).Forget();

        async UniTask RunAsync(Func<GomokuRoomSession, CancellationToken, UniTask> action)
        {
            if (session.IsBusy || paused || !gameObject.activeInHierarchy)
            {
                return;
            }

            GomokuRoomSession current = session;
            CancellationToken token = OpenCancellationToken;
            try
            {
                await action(current, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    status.text = current.NeedsQuery ? current.Notice : error.Message;
                }
            }
        }

        void Refresh()
        {
            if (OpenCancellationToken.IsCancellationRequested)
            {
                return;
            }

            GomokuRoomView snapshot = session.Snapshot;
            if (snapshot == null)
            {
                boardArea.Interactable = false;
                CloseSelf();
                return;
            }

            if (!ReferenceEquals(renderedGame, session.Game))
            {
                boardArea.Render(session.Game);
                renderedGame = session.Game;
            }
            int count = session.Game.Moves.Count;
            if (!paused && count > renderedMoves && !session.NeedsQuery && !session.LastUpdateWasBaseline)
            {
                GameAudio.TryPlayAsync(GameAudioIds.GomokuPlace, OpenCancellationToken).Forget();
            }

            renderedMoves = count;
            bool black = false;
            foreach (GomokuMemberView member in snapshot.Members)
            {
                if (member.MemberId == snapshot.RecipientMemberId)
                {
                    black = member.IsHost;
                }
            }

            bool ownTurn = session.Game.CurrentTurn == (black ? GomokuStone.Black : GomokuStone.White);
            boardArea.Interactable = !paused && !session.IsBusy && !session.NeedsQuery &&
                snapshot.State == GomokuRoomState.Playing && ownTurn;
            queryButton.interactable = !session.IsBusy;
            exitButton.interactable = !session.IsBusy && !session.NeedsQuery;
            string result;
            switch (snapshot.Outcome)
            {
                case GomokuOutcome.BlackVictory:
                    result = "黑方获胜";
                    break;
                case GomokuOutcome.WhiteVictory:
                    result = "白方获胜";
                    break;
                case GomokuOutcome.Draw:
                    result = "和棋";
                    break;
                case GomokuOutcome.Aborted:
                    result = "对局已中止";
                    break;
                default:
                    result = ownTurn ? "轮到你落子" : "等待对手落子";
                    break;
            }

            status.text = $"房间 {snapshot.RoomCode} · 你执{(black ? "黑" : "白")} · {count} 手\n{result}\n{session.Notice}";
        }
    }
}
