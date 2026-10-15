using GameFrame.UI;
using GameFrame.Diagnostics;

namespace Game.Hotfix
{
    public partial class RuntimeLogPanel : UIPanel<RuntimeLogBuffer>
    {
        protected override void OnCreate()
        {
            runtimeDebugConsole.CloseRequested += Close;
            LifetimeScope.Register(() => runtimeDebugConsole.CloseRequested -= Close);
        }

        protected override void OnOpen(RuntimeLogBuffer args)
        {
            runtimeDebugConsole.Bind(args);
        }

        protected override void OnClose()
        {
            runtimeDebugConsole.Unbind();
        }

        void Close()
        {
            CloseSelf();
        }
    }
}
