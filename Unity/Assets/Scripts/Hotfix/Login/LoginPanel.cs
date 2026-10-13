using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Hotfix.Network.Protocol;
using GameFrame.Network;
using GameFrame.UI;
using UnityEngine;

namespace Game.Hotfix
{
    public partial class LoginPanel : UIPanel<LoginPanelArgs>
    {
        private bool submitting;

        protected override void OnCreate()
        {
            confirmLoginButton.onClick.AddListener(ConfirmLogin);
            LifetimeScope.Register(() => confirmLoginButton.onClick.RemoveListener(ConfirmLogin));
        }

        protected override void OnOpen(LoginPanelArgs args)
        {
            submitting = false;
            accountInput.characterLimit = 64;
            passwordInput.characterLimit = 128;
            passwordInput.SetTextWithoutNotify(string.Empty);
            statusText.text = args.Message;
            SetInteractable(!args.Session.IsBusy);
            if (args.Session.IsBusy)
            {
                WaitForPreviousOperationAsync(args.Session, OpenCancellationToken).Forget();
            }
        }

        protected override void OnClose()
        {
            passwordInput.SetTextWithoutNotify(string.Empty);
        }

        private void ConfirmLogin()
        {
            if (submitting || Args.Session.IsBusy)
            {
                return;
            }

            if (!Args.Session.IsAuthenticated && !GameLoginSession.IsValidAccount(accountInput.text))
            {
                statusText.text = "账号需为 1～64 个字符，不能包含首尾空白或控制字符";
                accountInput.ActivateInputField();
                return;
            }

            if (!Args.Session.IsAuthenticated && !GameLoginSession.IsValidPassword(passwordInput.text))
            {
                statusText.text = "密码需为 8～128 个字符";
                passwordInput.ActivateInputField();
                return;
            }

            submitting = true;
            SetInteractable(false);
            statusText.text = Args.Session.IsAuthenticated ? "正在进入主场景…" : "正在登录…";
            SubmitAsync(Args, OpenCancellationToken).Forget();
        }

        private async UniTask SubmitAsync(LoginPanelArgs args, CancellationToken token)
        {
            try
            {
                if (!args.Session.IsAuthenticated)
                {
                    ResultCode result = await args.Session.LoginAsync(accountInput.text, passwordInput.text, token);
                    token.ThrowIfCancellationRequested();
                    if (result != ResultCode.Success)
                    {
                        statusText.text = result == ResultCode.InvalidCredentials ? "账号或密码错误" : "登录被拒绝，请重新登录";
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();
                statusText.text = "正在进入主场景…";
                passwordInput.SetTextWithoutNotify(string.Empty);
                await args.LoggedIn();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = args.Session.IsAuthenticated ? "进入主场景失败，请再次点击确定" :
                        error is NetworkRequestTimeoutException ? "登录超时，请再次点击确定" :
                        error is NetworkRemoteException || error is NetworkBackpressureException ? "服务繁忙，请稍后再试" :
                        "连接或登录失败，请检查网络与服务端";
                }

                // Credential failures are normal results; unexpected transport/cleanup faults
                // are recorded without account names, passwords or response credentials.
                Debug.LogException(error);
            }
            finally
            {
                // A cached panel can reopen while the old request is finishing its cleanup.
                if (!token.IsCancellationRequested)
                {
                    submitting = false;
                    passwordInput.SetTextWithoutNotify(string.Empty);
                    SetInteractable(true);
                }
            }
        }

        private void SetInteractable(bool value)
        {
            accountInput.interactable = value && !Args.Session.IsAuthenticated;
            passwordInput.interactable = value && !Args.Session.IsAuthenticated;
            confirmLoginButton.interactable = value;
        }

        private async UniTask WaitForPreviousOperationAsync(GameLoginSession session, CancellationToken token)
        {
            try
            {
                await UniTask.WaitUntil(() => !session.IsBusy, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                SetInteractable(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }
    }
}
