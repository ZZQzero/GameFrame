# MemoryPack

项目安装 MemoryPack `1.21.4`，用于 C# 二进制序列化；安装本身不改变现有业务或启动流程。

## 依赖与恢复

- `Unity/Packages/manifest.json` 固定 NuGetForUnity `4.5.0` 和 MemoryPack.Unity `1.21.4` 的 Git tag。
- `Unity/Assets/packages.config` 固定 MemoryPack、MemoryPack.Core、MemoryPack.Generator 及所需 NuGet 依赖。
- `Unity/Assets/NuGet.config` 配置 NuGet 官方源，安装目录为 `Unity/Assets/Packages`。
- 在 Unity 菜单 `NuGet > Manage NuGet Packages` 查看依赖；需要恢复时使用 `NuGet > Restore Packages`。

`Unity/Assets/Packages/` 中的依赖及其 `.meta` 纳入版本管理，保留 analyzer 标签、插件导入设置和 GUID；`packages.config`、`NuGet.config`、对应配置 `.meta` 和 UPM manifest/lock 一并提交。依赖缺失时可通过 NuGetForUnity 恢复。

MemoryPack.Core 使用 `.NET Standard 2.1` 版本。MemoryPack.Generator 是源码生成器，DLL 需要保留 `RoslynAnalyzer` 标签，并排除作为 Editor / Player 普通插件加载；这些导入设置由 NuGetForUnity 管理。不要把面向 `.NET 7` 及更高版本的运行时 DLL 替换到 Unity 中，也不要手改生成代码。

## 基本使用

业务 DTO 放在 `Game.Hotfix`，类型标记为 `partial`：

```csharp
using MemoryPack;

namespace Game.Hotfix
{
    [MemoryPackable]
    public partial class PlayerMessage
    {
        public long PlayerId { get; set; }
        public string DisplayName { get; set; }
    }
}
```

使用 `MemoryPackSerializer.Serialize(message)` 得到字节数组，使用 `MemoryPackSerializer.Deserialize<PlayerMessage>(bytes)` 还原。需要减少发送时的中间分配，可使用写入 `IBufferWriter<byte>` 的重载；缓冲生命周期由调用者管理。

默认模式按成员顺序编码。客户端和服务端必须使用一致的协议定义；需要双向兼容增删字段时，再选择 `GenerateType.VersionTolerant` 和固定的 `MemoryPackOrder`。无引用结构体按内存布局编码，不能把其字段布局当作可随意演进的协议。

Unity 扩展提供部分 Unity 类型支持。需要直接引用扩展 API 的 asmdef 应显式引用 `MemoryPack.Unity`。Unity 与 .NET 服务端互通时，避免官方列出的布局不兼容类型，例如 `DateTimeOffset` 和 `ValueTuple`。

## 验证边界

安装时已通过 Editor 编译，并在 `Game.Hotfix` 中用临时 DTO 验证源码生成、中文字符串 / `Vector3` / 数组往返、`IBufferWriter<byte>` 写入和已有对象复用。当前 `StandaloneOSX` 目标的 Player 脚本编译通过；临时验证代码和编译输出已清理。

Editor 编译与序列化验证不等于正式包验证。引入实际热更 DTO 后，还需要按当前 HybridCLR 流程重新生成热更 DLL、核对 AOT 泛型 / 元数据，并在 IL2CPP 包中验证热更新增类型与 Formatter 注册。

官方说明：[MemoryPack](https://github.com/Cysharp/MemoryPack)、[Unity 安装与限制](https://github.com/Cysharp/MemoryPack#unity)。
