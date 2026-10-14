using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using GameFrame.Network;
using GameFrame.UI;
using UnityEngine;

namespace Game.Hotfix
{
    public partial class LoginPanel : UIPanel<LoginPanelArgs>
    {
        private bool submitting;
        private bool preparing;
        private bool useRemembered;
        private string loginNotice;

        protected override void OnCreate()
        {
            confirmLoginButton.onClick.AddListener(ConfirmLogin);
            switchAccountButton.onClick.AddListener(SwitchAccount);
            LifetimeScope.Register(() => confirmLoginButton.onClick.RemoveListener(ConfirmLogin));
            LifetimeScope.Register(() => switchAccountButton.onClick.RemoveListener(SwitchAccount));
        }

        protected override void OnOpen(LoginPanelArgs args)
        {
            submitting = false;
            preparing = args.Session.IsBusy || args.Session.SupportsRememberedLogin;
            useRemembered = false;
            loginNotice = string.Empty;
            accountInput.characterLimit = 64;
            passwordInput.characterLimit = 128;
            passwordInput.SetTextWithoutNotify(string.Empty);
            statusText.text = args.Message;
            rememberedInfoText.text = args.Session.SupportsRememberedLogin ? "登录成功后记住账号，密码不保存" : string.Empty;
            RefreshControls();
            if (preparing)
            {
                PrepareLoginAsync(args, OpenCancellationToken).Forget();
            }
        }

        protected override void OnClose()
        {
            passwordInput.SetTextWithoutNotify(string.Empty);
        }

        private void ConfirmLogin()
        {
            if (preparing || submitting || Args.Session.IsBusy)
            {
                return;
            }

            if (!Args.Session.IsAuthenticated && !useRemembered && !GameLoginSession.IsValidAccount(accountInput.text))
            {
                statusText.text = "账号需为 1～64 个字符，不能包含首尾空白或控制字符";
                accountInput.ActivateInputField();
                return;
            }

            if (!Args.Session.IsAuthenticated && !useRemembered && !GameLoginSession.IsValidPassword(passwordInput.text))
            {
                statusText.text = "密码需为 8～128 个字符";
                passwordInput.ActivateInputField();
                return;
            }

            submitting = true;
            RefreshControls();
            statusText.text = Args.Session.IsAuthenticated ? "正在进入主场景…" : useRemembered ? "正在恢复登录…" : "正在登录…";
            SubmitAsync(Args, useRemembered, OpenCancellationToken).Forget();
        }

        private async UniTask SubmitAsync(LoginPanelArgs args, bool resume, CancellationToken token)
        {
            try
            {
                try
                {
                    if (!args.Session.IsAuthenticated)
                    {
                        GameLoginResult result = resume ? await args.Session.ResumeLoginAsync(token) :
                            await args.Session.LoginAsync(accountInput.text, passwordInput.text, token);
                        token.ThrowIfCancellationRequested();
                        if (result.ResultCode != ResultCode.Success)
                        {
                            statusText.text = resume ? "保存的登录已失效，请输入密码重新登录" :
                                result.ResultCode == ResultCode.InvalidCredentials ? "账号或密码错误" : "登录被拒绝，请重新登录";
                        }
                        else
                        {
                            loginNotice = result.RememberedLoginError == null ? string.Empty :
                                "本次未能记住账号，下次登录需输入密码";
                            if (result.RememberedLoginError != null)
                            {
                                Debug.LogException(result.RememberedLoginError);
                            }
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    if (args.Session.IsAuthenticated)
                    {
                        statusText.text = "正在进入主场景…";
                        passwordInput.SetTextWithoutNotify(string.Empty);
                        await args.LoggedIn(loginNotice);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    if (!token.IsCancellationRequested)
                    {
                        statusText.text = args.Session.IsAuthenticated ? "进入主场景失败，请再次点击确定" :
                            resume ? "恢复未完成，请输入密码重新登录" :
                            error is NetworkRequestTimeoutException ? "登录超时，请再次点击确定" :
                            error is NetworkRemoteException || error is NetworkBackpressureException ? "服务繁忙，请稍后再试" :
                            "连接或登录失败，请检查网络与服务端";
                    }

                    Debug.LogException(error);
                }

                if (!token.IsCancellationRequested && resume && !args.Session.IsAuthenticated)
                {
                    await RefreshRememberedAccountAsync(args, token);
                    if (!token.IsCancellationRequested && useRemembered)
                    {
                        statusText.text = "连接未完成，已保留免密资格，请再次点击确定";
                    }
                }
            }
            finally
            {
                // A cached panel can reopen while the old request is finishing its cleanup.
                if (!token.IsCancellationRequested)
                {
                    submitting = false;
                    passwordInput.SetTextWithoutNotify(string.Empty);
                    RefreshControls();
                }
            }
        }

        private async UniTask RefreshRememberedAccountAsync(LoginPanelArgs args, CancellationToken token)
        {
            useRemembered = false;
            rememberedInfoText.text = "账号已保留，请输入密码登录";
            try
            {
                RememberedAccount account = await args.Session.ReadRememberedAccountAsync(token);
                token.ThrowIfCancellationRequested();
                if (account != null)
                {
                    accountInput.SetTextWithoutNotify(account.AccountName);
                    useRemembered = account.CanResume;
                }

                rememberedInfoText.text = useRemembered ? "已记住此账号，点击确定登录" :
                    account != null ? "账号已保留，请输入密码登录" : "登录成功后记住账号，密码不保存";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = "读取本地账号失败，请检查本地存储";
                }

                Debug.LogException(error);
            }
        }

        private void RefreshControls()
        {
            bool value = !preparing && !submitting;
            accountInput.interactable = value && !Args.Session.IsAuthenticated && !useRemembered;
            passwordInput.gameObject.SetActive(!useRemembered);
            passwordLabel.gameObject.SetActive(!useRemembered);
            passwordInput.interactable = value && !Args.Session.IsAuthenticated && !useRemembered;
            confirmLoginButton.interactable = value;
            switchAccountButton.gameObject.SetActive(Args.Session.SupportsRememberedLogin && !Args.Session.IsAuthenticated);
            switchAccountButton.interactable = value;
        }

        private async UniTask PrepareLoginAsync(LoginPanelArgs args, CancellationToken token)
        {
            try
            {
                await UniTask.WaitUntil(() => !args.Session.IsBusy, cancellationToken: token);
                if (!args.Session.IsAuthenticated && args.Session.SupportsRememberedLogin)
                {
                    await RefreshRememberedAccountAsync(args, token);
                }

                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = "读取本地账号失败，请检查本地存储";
                }

                Debug.LogException(error);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    preparing = false;
                    RefreshControls();
                }
            }
        }

        private void SwitchAccount()
        {
            if (preparing || submitting || Args.Session.IsBusy)
            {
                return;
            }

            submitting = true;
            RefreshControls();
            SwitchAccountAsync(Args, OpenCancellationToken).Forget();
        }

        private async UniTask SwitchAccountAsync(LoginPanelArgs args, CancellationToken token)
        {
            try
            {
                await args.Session.ForgetRememberedAccountAsync(token);
                token.ThrowIfCancellationRequested();
                useRemembered = false;
                accountInput.SetTextWithoutNotify(string.Empty);
                passwordInput.SetTextWithoutNotify(string.Empty);
                rememberedInfoText.text = "登录成功后记住账号，密码不保存";
                statusText.text = string.Empty;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = "清除已保存账号失败，请再次点击切换账号";
                }

                Debug.LogException(error);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    submitting = false;
                    RefreshControls();
                }
            }
        }
    }
}
