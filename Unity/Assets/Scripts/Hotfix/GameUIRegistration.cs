using GameFrame.UI;

namespace Game.Hotfix
{
    /// <summary>业务面板的类型、资源地址、卸载分组与缓存配置统一在此维护。</summary>
    public static class GameUIRegistration
    {
        public static void RegisterAll()
        {
            GameUI.Register<LoginPanel>("LoginPanel", UIGroup.Scene, cache: true);
            GameUI.Register<MainPanel>("MainPanel", UIGroup.Scene, cache: true);
            GameUI.Register<WoodenFishMainPanel>("WoodenFishMain", UIGroup.Scene, cache: true);
            GameUI.Register<GomokuLobbyPanel>("GomokuLobby", UIGroup.Scene, cache: true);
            GameUI.Register<GomokuOnlinePanel>("GomokuOnline", UIGroup.Scene, cache: true);
            GameUI.Register<GomokuMainPanel>("GomokuMain", UIGroup.Scene, cache: true);
        }
    }
}
