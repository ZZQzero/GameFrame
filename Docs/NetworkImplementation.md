# 网络模块实施记录

设计契约见 [NetworkDesign.md](NetworkDesign.md)。本记录区分已实现代码、已执行验证和待实现项，不将协议单元测试视为完整网络验收。

## P0/P2 当前进度

2026-10-11 已实现以下独立部分：

- `Runtime/Network/Protocol/FrameCodec.cs`：V1 32 字节固定头、小端读写、分片头预检、精确消息长度、方向、CorrelationId、系统错误响应与通道边界。
- `Runtime/Network/Protocol/ControlCodec.cs`：六种系统控制包的正文编码与解析，WSS 空 ticket、KCP 1～1024 字节 ticket，Hello hash/限额/能力/心跳校验及 catalog 最大正文的协商检查。解析借用输入 Span，不向业务交付借用缓冲。
- `Runtime/Network/ProtocolContracts/NetProtocolContracts.cs`：字符串/数组限额属性和固定硬预算。属性构造校验不替代后续生成工具的完整 schema 分析。
- `Runtime/Network/NetworkException.cs`：协议、背压、请求超时与远端系统错误类型；未知非零远端码保留原值。
- `Runtime/Network/NetworkOptions.cs` 与 `NetworkConnectOptions.cs`：宿主/连接预算、可信端点、连接参数与 grant 单调期限校验；尚未接入实际连接与资源配额执行。
- `Protocol/Session/*.proto` 与生成的 `Hotfix/Network/Generated/GameNetMessages.cs`：17 个显式顺序 MemoryPack DTO、11 个登录/恢复/授权/世界/echo 路由。移动输入包含 commandId、EntityId、WorldEpoch；当前仅声明不可靠通道。
- `Runtime/Network/Messaging/NetProtocolCatalog.cs`：不可变强类型路由、codec 与 catalog，验证重复 opcode、目录路由归属与最大正文。
- `Runtime/Network/ProtocolContracts/NetWireReader.cs` 与生成的验证器：反序列化前检查对象布局、数组/字符串长度、UTF-8、深度/节点预算与尾随字节；序列化前检查 DTO 约束。
- `Tools/NetworkProtocol`：以 Proto 为唯一手写源，使用 protobuf-net.Reflection 3.2.52 解析、生成 C# DTO，使用 Roslyn 4.14.0 和 MemoryPack.Generator 1.21.4 编译 DTO 与完整生成代码，分析实际成员并核对序列化信息；输出强类型 codec/catalog、规范 manifest、SHA-256 与 51 组默认/普通/最大值向量；`check` 校验生成产物一致性。拒绝 Nullable<T>、递归、缺少限额、重复 opcode、自定义 formatter 与序列化回调。目前限于顶层非泛型 DTO 和一维非交错数组。
- `DotNet/DotNet.csproj`：通过 `Protocol/GameFrame.Protocol.projitems` 编译相同生成源码与纯协议基础代码，MemoryPack 1.21.4 依赖锁文件已保存。入口仍未实现 WSS 服务。
- `Tools/NetworkProtocol.Tests`：链接相同 Runtime 源码与协议测试，使用 .NET 10；Tools/DotNet 分别锁定 SDK 10.0.401，测试依赖锁文件纳入版本管理，不改变 Luban 的 .NET 8 环境。

协议工具清理：命令入口与 MemoryPack 代码生成实现已拆分，后者命名为 `MemoryPackProtocolGenerator`；连接配置和帧控制代码移至测试工程编译，生成工具只链接协议契约、异常与 catalog。参数快照测试移至独立的 `NetworkOptionsTests`，生成产物与协议格式不变。这里的代码生成实现不代表 DotNet 服务端已实现。

辅助产物已移至仓库根 `Protocol/Generated/`。默认 generate/check 只处理两个正式 C# 文件，`--artifacts` 才处理 manifest/hash/vector；正式代码生成在内存中计算规范清单与 hash，不生成测试向量。两种模式的 C# 输出一致，运行时指纹内嵌于 catalog，不读取辅助文件。Unity/.NET 向量测试均读取外部 `golden-vectors.json`。

尚未实现 NetworkRuntime/NetworkClient/ConnectionContext、实际传输、有界资源与 pending、连接状态机、原生 ABI/DTLS、服务端与 Hotfix 会话。ControlCodec 已提供连接阶段控制包校验，但尚未接入真实状态机。有界序列化 writer、完整生成工具负例/字段差异报告/CI 与 HybridCLR 验证仍待实施，不能宣称 P2 或完整网络模块验收完成。

待用户确认：设计要求 WSS-only 与 KCP 两种世界模式，但目前移动输入只登记不可靠路由，WSS 禁止发送或自动转换该通道。建议新增独立的可靠移动 Notify 路由，由 WSS 世界模式显式使用；KCP 保留现有不可靠路由。该决定影响公开协议，在确认前不冻结最终目录。

## 已确认的业务输入

用户确认第一版本地联调使用账号＋密码，只在显式 local profile 启用；生产未配置认证时拒绝启动。生产账号校验服务与部署域名/证书尚未确定。

## 已解决：可空数值编码

核对 MemoryPack 1.21.4 的 [NullableFormatter](https://github.com/Cysharp/MemoryPack/blob/1.21.4/src/MemoryPack.Core/Formatters/NullableFormatter.cs) 后发现：对不包含引用的 T，Nullable<T> 通过 DangerousWriteUnmanaged 原样写入内存，包含布局填充，而不是固定的 HasValue 字节加紧密排列的 Value。

在 .NET 10.0.401/macOS arm64、NuGet MemoryPack 1.21.4 下，用相同 `int?` 值 42 验证：

```text
初始编码：                   010000002A000000
仅改填充字节后的同值编码：   01AABBCC2A000000
null int?：                  0000000000000000
long? 42：                   01000000000000002A00000000000000
```

复现通过 Unsafe.As 修改 Nullable<int> 中 Value 之前的三个填充字节，数值仍为 42。该实验说明填充参与线上格式；不是声称所有普通构造都会产生非零填充，也不是已经证实所有发布平台布局不同。

用户确认将 Nullable<T> 移出 V1 白名单，改用显式 HasValue＋Value 或可空 DTO；设计第 8.1 节已同步修改。协议工具必须拒绝 Nullable<T>，不能自行增加 Formatter 或静默按平台布局解码。

## 实际验证

2026-10-11：

- .NET 10 测试 165/165 通过，包含 73 个协议/参数用例、59 个 MemoryPack/catalog 用例与 33 个工具工程用例；默认与 `--artifacts` 的生成、检查均通过，覆盖两种模式 C# 一致性及默认模式不写辅助文件。
- Unity 6000.3.19f1 Editor 编译通过，新增 Console 错误为 0；EditMode `GameFrame.Tests.NetworkProtocolTests` 与 `NetworkMemoryPackTests` 共 132/132 通过。
- 辅助文件迁移后重新验证 Editor 编译与 `NetworkMemoryPackTests`：59/59 通过，覆盖外部路径的 51 组向量；本次未重复 Player/IL2CPP 或实际网络验证。
- Unity 的 MemoryPack.Core .NET Standard 版本与 .NET 10 对 51 组向量输出字节一致；恶意数组/字符串声明、无效 UTF-8/字符计数、根/元素空值、尾随字节、catalog 初始化与 hash 复制检查通过。
- DotNet .NET 10 项目编译通过，包含同源生成协议；不代表服务端功能已完成。
- StandaloneOSX Player 脚本编译通过，共 43 个程序集，包含 GameFrame.Runtime 与 Game.Hotfix。仅验证脚本编译，不代表原生 IL2CPP 打包或真机通过。
- 用例包含设计发布的 Ping 小端向量、截断/尾随/拼帧、未知头字段、声明长度溢出、32 KiB/1150 字节边界、方向/握手限制、WSS/KCP ticket、协商 hash/路由上限、控制正文保留位，以及 seed=467 的 4096 次随机短包。
- 测试前后均为 `Assets/Scenes/Launch.unity`、未播放；场景未修改。EditorSettings 的 enterPlayModeOptionsEnabled=false、enterPlayModeOptions=None 保持原值。
- 未执行 PlayMode、原生插件构建、真实 WSS/UDP 联调、IL2CPP、真机、24 小时 soak 或容量测试。

从仓库根目录执行已有测试：

```sh
(cd Tools && dotnet restore NetworkProtocol.Tests --locked-mode)
(cd Tools && dotnet run --project NetworkProtocol -- generate --artifacts)
(cd Tools && dotnet run --project NetworkProtocol -- check --artifacts)
(cd Tools && dotnet test NetworkProtocol.Tests --no-restore)
```

Unity Test Runner 选择 `GameFrame.Tests.NetworkProtocolTests` 与 `GameFrame.Tests.NetworkMemoryPackTests`；不能将空测试结果算作通过。
