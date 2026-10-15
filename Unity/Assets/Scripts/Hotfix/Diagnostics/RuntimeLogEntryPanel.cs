using GameFrame.UI;
using GameFrame.Diagnostics;
using Cysharp.Threading.Tasks;

namespace Game.Hotfix
{
    public partial class RuntimeLogEntryPanel : UIPanel<UINone>
    {
        protected override void OnCreate()
        {
            openButton.onClick.AddListener(OpenLogs);
            LifetimeScope.Register(() => openButton.onClick.RemoveListener(OpenLogs));
        }

        protected override void OnOpen(UINone args)
        {
        }

        void OpenLogs()
        {
            GameUI.Guide<RuntimeLogPanel, RuntimeLogBuffer>(RuntimeLogCapture.Buffer).Forget();
        }
    }
}
