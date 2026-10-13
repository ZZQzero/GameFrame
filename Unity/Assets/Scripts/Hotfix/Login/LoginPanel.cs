using GameFrame.UI;

namespace Game.Hotfix
{
    public partial class LoginPanel : UIPanel<UINone>
    {
        protected override void OnCreate()
        {
            confirmLoginButton.onClick.AddListener(ConfirmLogin);
            LifetimeScope.Register(() => confirmLoginButton.onClick.RemoveListener(ConfirmLogin));
        }

        protected override void OnOpen(UINone args)
        {
            passwordInput.SetTextWithoutNotify(string.Empty);
            statusText.text = string.Empty;
        }

        protected override void OnClose()
        {
            passwordInput.SetTextWithoutNotify(string.Empty);
        }

        private void ConfirmLogin()
        {
            if (string.IsNullOrWhiteSpace(accountInput.text))
            {
                statusText.text = "请输入账号";
                accountInput.ActivateInputField();
                return;
            }

            if (string.IsNullOrEmpty(passwordInput.text))
            {
                statusText.text = "请输入密码";
                passwordInput.ActivateInputField();
                return;
            }

            // 当前仅提供登录界面；接入认证服务后在这里提交账号和密码。
            statusText.text = "登录服务尚未接入";
        }
    }
}
