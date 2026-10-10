using GameFrame.UI;

namespace Game.Hotfix
{
    /// <summary>业务面板的类型、资源地址、卸载分组与缓存配置统一在此维护。</summary>
    public static class GameUIRegistration
    {
        public static void RegisterAll()
        {
            GameUI.Register<WoodenFishMainPanel>("WoodenFishMain", UIGroup.Scene, cache: true);
            GameUI.Register<GomokuMainPanel>("GomokuMain", UIGroup.Scene, cache: true);
        }
    }
}
