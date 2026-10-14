using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using GameFrame;
using GameFrame.Scene;
using GameFrame.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Hotfix
{
    /// <summary>登录与主界面的业务流程。借用账号会话，全部接口在主线程调用。</summary>
    public sealed class GameFlow
    {
        readonly GameLoginSession loginSession;
        readonly GomokuRoomSession gomoku;
        AsyncLazy enteringMain;
        AsyncLazy enteringGomoku;
        Task stopping;
        bool started;

        // 退出回调顺序不固定，框架可能先于业务流程关闭。
        bool CanNavigate => started && GameRuntime.IsInited && GameUI.IsInited;

        public GameFlow(GameLoginSession loginSession, GomokuRoomSession gomoku)
        {
            this.loginSession = loginSession ?? throw new ArgumentNullException(nameof(loginSession));
            this.gomoku = gomoku ?? throw new ArgumentNullException(nameof(gomoku));
        }

        public UniTask StartAsync()
        {
            if (stopping != null)
            {
                throw new ObjectDisposedException(nameof(GameFlow));
            }

            if (started)
            {
                throw new InvalidOperationException("[GameFlow] 业务流程已启动。");
            }

            started = true;
            loginSession.AuthenticationLost += OnAuthenticationLost;
            return OpenLoginAsync();
        }

        async UniTask OpenLoginAsync(string message = "")
        {
            if (!CanNavigate)
            {
                return;
            }

            await GameUI.Push<LoginPanel, LoginPanelArgs>(new LoginPanelArgs(loginSession, OpenMainAsync, message));
            if (!CanNavigate && GameUI.IsInited)
            {
                GameUI.Close<LoginPanel>();
            }
        }

        async UniTask OpenMainAsync(string message)
        {
            if (enteringMain != null)
            {
                await enteringMain.Task;
            }

            if (!CanNavigate)
            {
                return;
            }

            var pending = UniTask.Lazy(() => OpenMainCoreAsync(message));
            enteringMain = pending;
            try
            {
                await pending.Task;
            }
            finally
            {
                if (ReferenceEquals(enteringMain, pending))
                {
                    enteringMain = null;
                }
            }
        }

        async UniTask OpenMainCoreAsync(string message)
        {
            if (!CanNavigate)
            {
                return;
            }

            GameUser user = loginSession.CurrentUser;
            if (user == null)
            {
                throw new InvalidOperationException("[GameFlow] 账号会话已失效，不能进入主界面。");
            }

            // 场景加载不能取消；重新登录先等上一次切场结束，旧账号的完成结果不能导航。
            await GameScene.WaitForIdleAsync();
            if (!CanNavigate || !ReferenceEquals(loginSession.CurrentUser, user))
            {
                return;
            }

            if (!GameScene.IsLoaded("Main"))
            {
                await GameScene.LoadAsync("Main", LoadSceneMode.Single);
            }

            if (!CanNavigate)
            {
                return;
            }

            // Single 加载会销毁 Launch 场景的相机，登录和主面板共用持久 UI 根节点。
            GameUI.ConfigureURPCameraStack(Camera.main);
            if (!ReferenceEquals(loginSession.CurrentUser, user))
            {
                return;
            }

            await GameUI.Push<MainPanel, MainPanelArgs>(new MainPanelArgs(OpenGomokuAsync, message));
            if (!CanNavigate || !ReferenceEquals(loginSession.CurrentUser, user))
            {
                if (GameUI.IsInited)
                {
                    GameUI.Close<MainPanel>();
                }

                return;
            }

            GameUI.Close<LoginPanel>();
        }

        async UniTask OpenGomokuAsync()
        {
            if (enteringGomoku != null)
            {
                await enteringGomoku.Task;
                return;
            }

            if (!CanNavigate)
            {
                return;
            }

            var pending = UniTask.Lazy(OpenGomokuCoreAsync);
            enteringGomoku = pending;
            try
            {
                await pending.Task;
            }
            finally
            {
                if (ReferenceEquals(enteringGomoku, pending))
                {
                    enteringGomoku = null;
                }
            }
        }

        async UniTask OpenGomokuCoreAsync()
        {
            if (!CanNavigate || !loginSession.IsAuthenticated)
            {
                return;
            }

            GameUser user = loginSession.CurrentUser;
            await GameUI.Push<GomokuLobbyPanel, GomokuRoomSession>(gomoku);
            if ((!CanNavigate || !ReferenceEquals(user, loginSession.CurrentUser)) && GameUI.IsInited)
            {
                GameUI.Close<GomokuLobbyPanel>();
            }
        }

        void OnAuthenticationLost()
        {
            if (!CanNavigate)
            {
                return;
            }

            GameUI.CloseGroup(UIGroup.Scene);
            OpenLoginAsync("会话已结束，请重新登录").Forget();
        }

        /// <summary>停止导航并等待已发起的切场；账号会话由创建方随后释放。</summary>
        public UniTask StopAsync()
        {
            if (stopping == null)
            {
                var completed = new TaskCompletionSource<bool>();
                stopping = completed.Task;
                _ = StopCoreAsync(completed);
            }

            return stopping.AsUniTask();
        }

        async Task StopCoreAsync(TaskCompletionSource<bool> completed)
        {
            started = false;
            loginSession.AuthenticationLost -= OnAuthenticationLost;
            Exception failure = null;
            try
            {
                if (GameUI.IsInited)
                {
                    GameUI.CloseGroup(UIGroup.Scene);
                }
            }
            catch (Exception error)
            {
                failure = error;
            }

            try
            {
                if (enteringMain != null)
                {
                    await enteringMain.Task;
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
                    Debug.LogException(error);
                }
            }

            try
            {
                if (enteringGomoku != null)
                {
                    await enteringGomoku.Task;
                }
            }
            catch (OperationCanceledException) when (!started)
            {
                // CloseGroup cancels the UI load started by this flow.
            }
            catch (Exception error)
            {
                if (failure == null)
                {
                    failure = error;
                }
                else
                {
                    Debug.LogException(error);
                }
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
