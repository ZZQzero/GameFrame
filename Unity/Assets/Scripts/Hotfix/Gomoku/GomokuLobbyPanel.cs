using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Hotfix.Network.Protocol;
using GameFrame.UI;

namespace Game.Hotfix
{
    public partial class GomokuLobbyPanel : UIPanel<GomokuRoomSession>
    {
        GomokuRoomSession session;
        bool navigating;
        bool openingOnline;

        protected override void OnCreate()
        {
            localButton.onClick.AddListener(OpenLocal);
            createButton.onClick.AddListener(CreateRoom);
            joinButton.onClick.AddListener(JoinRoom);
            readyButton.onClick.AddListener(Ready);
            startButton.onClick.AddListener(StartGame);
            queryButton.onClick.AddListener(Query);
            exitButton.onClick.AddListener(Exit);
            LifetimeScope.Register(() => localButton.onClick.RemoveListener(OpenLocal));
            LifetimeScope.Register(() => createButton.onClick.RemoveListener(CreateRoom));
            LifetimeScope.Register(() => joinButton.onClick.RemoveListener(JoinRoom));
            LifetimeScope.Register(() => readyButton.onClick.RemoveListener(Ready));
            LifetimeScope.Register(() => startButton.onClick.RemoveListener(StartGame));
            LifetimeScope.Register(() => queryButton.onClick.RemoveListener(Query));
            LifetimeScope.Register(() => exitButton.onClick.RemoveListener(Exit));
        }

        protected override void OnOpen(GomokuRoomSession args)
        {
            session = args ?? throw new ArgumentNullException(nameof(args));
            session.Activate();
            session.Changed += Refresh;
            GomokuRoomSession current = session;
            OpenScope.Register(() => current.Changed -= Refresh);
            navigating = false;
            openingOnline = false;
            Refresh();
        }

        protected override void OnResume()
        {
            Refresh();
        }

        void OpenLocal() => OpenLocalAsync().Forget();
        async UniTask OpenLocalAsync()
        {
            if (navigating || session.IsBusy || session.Snapshot != null || session.NeedsQuery)
            {
                return;
            }

            navigating = true;
            CancellationToken token = OpenCancellationToken;
            try
            {
                using var cancellation = token.Register(() =>
                {
                    if (GameUI.IsInited)
                    {
                        GameUI.Close<GomokuMainPanel>();
                    }
                });
                await GameUI.Push<GomokuMainPanel>();
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = "打开本地棋盘失败：" + error.Message;
                }
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    navigating = false;
                }
            }
        }

        void CreateRoom() => RunAsync(async (current, token) =>
        {
            await current.CreateAsync(token);
        }).Forget();
        void JoinRoom() => RunAsync(async (current, token) =>
        {
            await current.JoinAsync(roomCodeInput.text, token);
        }).Forget();
        void Ready() => RunAsync(async (current, token) =>
        {
            GomokuMemberView own = FindOwn(current);
            await current.ReadyAsync(!own.Ready, token);
        }).Forget();
        void StartGame() => RunAsync(async (current, token) =>
        {
            await current.StartAsync(token);
        }).Forget();
        void Query() => RunAsync((current, token) => current.QueryAsync(token)).Forget();
        void Exit() => RunAsync(async (current, token) =>
        {
            if (current.Snapshot != null)
            {
                GomokuResult result = await current.LeaveAsync(token);
                if (result != GomokuResult.Ok)
                {
                    return;
                }
            }

            if (!current.NeedsQuery && !token.IsCancellationRequested)
            {
                CloseSelf();
            }
        }).Forget();

        async UniTask RunAsync(Func<GomokuRoomSession, CancellationToken, UniTask> action)
        {
            if (session.IsBusy || navigating || !gameObject.activeInHierarchy)
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
                    statusText.text = current.NeedsQuery ? current.Notice : error.Message;
                }
            }
        }

        GomokuMemberView FindOwn(GomokuRoomSession current)
        {
            foreach (GomokuMemberView member in current.Snapshot.Members)
            {
                if (member.AccountId == current.AccountId)
                {
                    return member;
                }
            }

            throw new InvalidOperationException("当前房间缺少本人资格。");
        }

        void Refresh()
        {
            if (OpenCancellationToken.IsCancellationRequested)
            {
                return;
            }

            GomokuRoomView snapshot = session.Snapshot;
            bool room = snapshot != null;
            bool available = !session.IsBusy && !session.NeedsQuery && !navigating;
            localButton.gameObject.SetActive(!room);
            createButton.gameObject.SetActive(!room);
            joinButton.gameObject.SetActive(!room);
            roomCodeInput.gameObject.SetActive(!room);
            localButton.interactable = available;
            createButton.interactable = available;
            joinButton.interactable = available;
            readyButton.gameObject.SetActive(room && snapshot.State == GomokuRoomState.Waiting);
            startButton.gameObject.SetActive(room && snapshot.State == GomokuRoomState.Waiting);
            readyButton.interactable = available;
            startButton.interactable = available && room && FindOwn(session).IsHost && snapshot.Members.Count == 2 &&
                snapshot.Members[0].Ready && snapshot.Members[1].Ready;
            queryButton.interactable = !session.IsBusy;
            exitButton.interactable = available;
            exitButton.GetComponentInChildren<TMPro.TextMeshProUGUI>().text = room ? "退出房间" : "返回";
            string state = room ? $"房间号：{snapshot.RoomCode}\n" : "本地双人，或创建 / 加入房间\n";
            if (room)
            {
                foreach (GomokuMemberView member in snapshot.Members)
                {
                    state += $"{(member.IsHost ? "黑方 / 房主" : "白方")} {member.AccountId} · {(member.Ready ? "已准备" : "未准备")}\n";
                }

                if (snapshot.Members.Count == 1)
                {
                    state += "等待另一位玩家加入\n";
                }
            }

            statusText.text = state + session.Notice;
            if (room && snapshot.State != GomokuRoomState.Waiting && gameObject.activeInHierarchy && !openingOnline)
            {
                OpenOnlineAsync().Forget();
            }
        }

        async UniTask OpenOnlineAsync()
        {
            openingOnline = true;
            var token = OpenCancellationToken;
            GomokuRoomSession current = session;
            try
            {
                using var cancellation = token.Register(() =>
                {
                    if (GameUI.IsInited)
                    {
                        GameUI.Close<GomokuOnlinePanel>();
                    }
                });
                GomokuOnlinePanel panel = await GameUI.Push<GomokuOnlinePanel, GomokuRoomSession>(current);
                if (!token.IsCancellationRequested && current.Snapshot == null)
                {
                    panel.CloseSelf();
                }
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = "打开棋盘失败，可点击查询状态再次进入：" + error.Message;
                }
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    openingOnline = false;
                }
            }
        }
    }
}
