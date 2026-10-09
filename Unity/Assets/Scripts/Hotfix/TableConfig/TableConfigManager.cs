using System;
using System.Threading;
using GameFrame.AOT;

namespace GameFrame.Hotfix.TableConfig
{
    /// <summary>Luban 配置的显式生命周期；表名由生成的 Tables 决定。</summary>
    public static class TableConfigManager
    {
        public static Tables Tables { get; private set; }

        public static void Init(ResourceLoadManager resources, CancellationToken token = default)
        {
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }
            if (Tables != null)
            {
                throw new InvalidOperationException("[TableConfig] 配置已初始化。");
            }
            token.ThrowIfCancellationRequested();
            Tables = new Tables(name =>
            {
                token.ThrowIfCancellationRequested();
                return resources.LoadConfigByte(name);
            });
        }

        public static void Shutdown() => Tables = null;
    }
}
