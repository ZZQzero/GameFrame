using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Protocol;
using GameFrame.Network;
using UnityEngine;

namespace Game.Hotfix
{
    /// <summary>Hotfix 账号会话。入口拥有实例，面板和业务借用；全部接口在主线程调用。</summary>
    public sealed class GameLoginSession : IAsyncDisposable
    {
        private readonly NetworkClient client;
        private readonly NetworkConnectOptions connectOptions;
        private readonly IDisposable stateObserver;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly RememberedLoginStore rememberedLogin;
        private GameUser user;
        private LoginResponse credentials;
        private Task operation;
        private Task disposal;

        public event Action AuthenticationLost;

        public bool IsBusy => operation != null;
        public bool IsAuthenticated => user != null && client.State == NetworkClientState.Connected &&
            NetworkClock.GetMilliseconds() < user.AccountDeadlineMilliseconds;
        public GameUser CurrentUser => IsAuthenticated ? user : null;

        /// <summary>借用已认证大厅客户端；调用方不关闭或释放它。</summary>
        public NetworkClient LobbyClient => IsAuthenticated ? client :
            throw new InvalidOperationException("当前账号尚未登录或会话已失效。");

        public GameLoginSession(NetworkRuntime runtime, Uri endpoint, bool allowDevelopmentWebSocket = false,
            RememberedLoginStore rememberedLogin = null)
        {
            RequireMainThread();
            if (runtime == null)
            {
                throw new ArgumentNullException(nameof(runtime));
            }

            connectOptions = NetworkConnectOptions.WebSocket(endpoint, allowDevelopmentWebSocket);
            this.rememberedLogin = rememberedLogin;
            client = runtime.CreateClient(new NetworkClientOptions
            {
                TrustedWebSocketEndpoints = new[] { endpoint }
            }, GameNetProtocol.Catalog);
            stateObserver = client.ObserveState(OnTransportState);
        }

        public static bool IsValidAccount(string account) => !string.IsNullOrWhiteSpace(account) &&
            account.Length <= 64 && account == account.Trim() && !account.Any(char.IsControl);

        public static bool IsValidPassword(string password) => password != null && password.Length >= 8 && password.Length <= 128;

        public bool SupportsRememberedLogin => rememberedLogin != null;

        public async UniTask<RememberedAccount> ReadRememberedAccountAsync(CancellationToken token = default)
        {
            RequireMainThread();
            TaskCompletionSource<bool> completed = BeginOperation();
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
                return rememberedLogin == null ? null : await rememberedLogin.ReadAsync(cancellation.Token);
            }
            finally
            {
                operation = null;
                completed.TrySetResult(true);
            }
        }

        public async UniTask ForgetRememberedAccountAsync(CancellationToken token = default)
        {
            RequireMainThread();
            TaskCompletionSource<bool> completed = BeginOperation();
            try
            {
                if (rememberedLogin != null)
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
                    await rememberedLogin.ForgetAsync(cancellation.Token);
                }
            }
            finally
            {
                operation = null;
                completed.TrySetResult(true);
            }
        }

        public UniTask<ResultCode> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
        {
            RequireMainThread();
            if (!IsValidAccount(account) || !IsValidPassword(password))
            {
                throw new ArgumentException("账号或密码不符合输入要求。");
            }

            return AuthenticateAsync(account, password, false, cancellationToken);
        }

        public UniTask<ResultCode> ResumeLoginAsync(CancellationToken cancellationToken = default)
        {
            RequireMainThread();
            if (rememberedLogin == null)
            {
                throw new InvalidOperationException("未配置账号恢复存储。");
            }

            return AuthenticateAsync(null, null, true, cancellationToken);
        }

        private async UniTask<ResultCode> AuthenticateAsync(string account, string password, bool resume,
            CancellationToken cancellationToken)
        {
            if (user != null)
            {
                throw new InvalidOperationException("请先结束旧账号会话，再登录其他账号。");
            }

            TaskCompletionSource<bool> completed = BeginOperation();
            LoginRequest request = null;
            ResumeLoginRequest resumeRequest = null;
            RememberedLoginCredential saved = null;
            LoginResponse response = null;
            Exception failure = null;
            bool committed = false;
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
                cancellation.Token.ThrowIfCancellationRequested();
                if (resume)
                {
                    saved = await rememberedLogin.TakeAsync(cancellation.Token);
                    if (saved == null)
                    {
                        return ResultCode.InvalidCredentials;
                    }

                    account = saved.Account.AccountName;
                    resumeRequest = new ResumeLoginRequest { AccountSessionId = saved.SessionId, ResumeToken = saved.Token };
                }
                else
                {
                    request = new LoginRequest { Account = account, Password = password };
                }

                await client.CloseAsync();
                await client.ConnectAsync(connectOptions, cancellation.Token);
                long admission = NetworkClock.GetMilliseconds();
                response = resume
                    ? await client.RequestAsync(GameNetRoutes.ResumeLogin, resumeRequest, TimeSpan.FromSeconds(5), cancellation.Token)
                    : await client.RequestAsync(GameNetRoutes.Login, request, TimeSpan.FromSeconds(5), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (response.ResultCode != ResultCode.Success)
                {
                    return response.ResultCode;
                }

                ValidateLogin(response, admission);
                if (saved != null && response.AccountId != saved.Account.AccountId)
                {
                    throw new NetworkProtocolException("恢复响应与保存的账号不一致。");
                }

                if (rememberedLogin != null)
                {
                    long remaining = checked(admission + response.ResumeRemainingTtlMs - NetworkClock.GetMilliseconds());
                    await rememberedLogin.SaveAsync(account, response.AccountId,
                        checked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + remaining),
                        response.AccountSessionId, response.ResumeToken, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    ValidateLogin(response, admission);
                }

                if (client.State != NetworkClientState.Connected)
                {
                    throw new NetworkException("登录响应到达后连接已失效。");
                }

                user = new GameUser(response.AccountId, account, response.AuthEpoch,
                    checked(admission + response.AccountRemainingTtlMs));
                credentials = response;
                response = null;
                committed = true;
                return ResultCode.Success;
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                if (request != null)
                {
                    request.Password = string.Empty;
                }

                saved?.Dispose();
                password = string.Empty;
                ClearCredentials(response);
                try
                {
                    if (!committed)
                    {
                        await CloseAfterFailureAsync(failure);
                    }
                }
                finally
                {
                    operation = null;
                    completed.TrySetResult(true);
                }
            }
        }

        public async UniTask<ResultCode> LogoutAsync(CancellationToken cancellationToken = default)
        {
            RequireMainThread();
            if (!IsAuthenticated)
            {
                throw new InvalidOperationException("当前账号尚未登录。");
            }

            TaskCompletionSource<bool> completed = BeginOperation();
            Exception failure = null;
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
                if (rememberedLogin != null)
                {
                    await rememberedLogin.ForgetAsync(cancellation.Token);
                }

                OperationResponse response = await client.RequestAsync(GameNetRoutes.Logout, new LogoutRequest(),
                    TimeSpan.FromSeconds(5), cancellation.Token);
                return response.ResultCode;
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                bool notify = user != null;
                ClearAuthentication();
                try
                {
                    await CloseAfterFailureAsync(failure);
                }
                finally
                {
                    operation = null;
                    completed.TrySetResult(true);
                    if (notify)
                    {
                        NotifyAuthenticationLost();
                    }
                }
            }
        }

        private TaskCompletionSource<bool> BeginOperation()
        {
            if (disposal != null)
            {
                throw new ObjectDisposedException(nameof(GameLoginSession));
            }

            if (operation != null)
            {
                throw new InvalidOperationException("账号操作正在执行。");
            }

            var completed = new TaskCompletionSource<bool>();
            operation = completed.Task;
            return completed;
        }

        private async UniTask CloseAfterFailureAsync(Exception failure)
        {
            try
            {
                await client.CloseAsync();
            }
            catch (Exception cleanup) when (failure != null)
            {
                Debug.LogException(new InvalidOperationException("账号连接清理失败，原操作仍报告首因。", cleanup));
            }
        }

        private static void ValidateLogin(LoginResponse response, long admission)
        {
            if (response.AccountId == 0 || response.AuthEpoch == 0 ||
                response.AccountSessionId == null || response.AccountSessionId.Length != 16 ||
                response.ResumeToken == null || response.ResumeToken.Length != 32 ||
                response.AccountRemainingTtlMs == 0 || response.AccountRemainingTtlMs > 86400000 ||
                response.ResumeRemainingTtlMs == 0 || response.ResumeRemainingTtlMs > response.AccountRemainingTtlMs ||
                NetworkClock.GetMilliseconds() >= checked(admission + response.ResumeRemainingTtlMs))
            {
                throw new NetworkProtocolException("服务端返回了无效或已过期的登录会话。");
            }
        }

        private void OnTransportState(NetworkStateChange change)
        {
            if (user != null && change.State != NetworkClientState.Connected)
            {
                ClearAuthentication();
                NotifyAuthenticationLost();
            }
        }

        private void NotifyAuthenticationLost()
        {
            try
            {
                AuthenticationLost?.Invoke();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        private void ClearAuthentication()
        {
            user = null;
            ClearCredentials(credentials);
            credentials = null;
        }

        private static void ClearCredentials(LoginResponse response)
        {
            if (response == null)
            {
                return;
            }

            if (response.AccountSessionId != null)
            {
                CryptographicOperations.ZeroMemory(response.AccountSessionId);
            }

            if (response.ResumeToken != null)
            {
                CryptographicOperations.ZeroMemory(response.ResumeToken);
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

        private async Task DisposeCoreAsync(TaskCompletionSource<bool> completed)
        {
            Exception failure = null;
            ClearAuthentication();
            AuthenticationLost = null;
            try
            {
                lifetime.Cancel();
            }
            catch (Exception error)
            {
                failure = error;
            }

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
                    Debug.LogException(error);
                }
            }

            try
            {
                stateObserver.Dispose();
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
                await client.DisposeAsync();
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
            finally
            {
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

        private static void RequireMainThread()
        {
            if (!PlayerLoopHelper.IsMainThread)
            {
                throw new InvalidOperationException("账号会话必须在 Unity 主线程使用。");
            }
        }
    }
}
