# MemoryPack 协议编写与生成

`Protocol/**/*.proto` 是唯一手写协议源。参考 `gameprotomessage` / `protomessage` 的 message、service、选项与 generate/check 组织方式；线上正文为 MemoryPack 1.21.4 UTF-8，帧协议为 [GFNP V1](../Docs/NetworkDesign.md)。Proto 仅是 IDL，不生成 Protobuf DTO，不使用 Protobuf Binary 或 MessagePack。

## 目录与双端使用

- `Common/net_options.proto`：全项目唯一的路由、长度、可空性和收窄整数选项。
- `Common/ResultProto.proto`：唯一业务结果码枚举与简单操作响应。
- `Session/SessionProto.proto`、`SessionService.proto`：账号登录、恢复、世界授权与注销。
- `World/WorldProto.proto`、`WorldService.proto`：世界入场/恢复/离场、实体成员事件、输入与快照。
- `Diagnostics/DiagnosticsProto.proto`、`DiagnosticsService.proto`：仅 local profile 注册的 Echo 联调路由。
- 各域按 message/service 分离；opcode 放在 method 上，DTO 可被多条路由复用。
- `GameFrame.Protocol.projitems`：仅包含生成 C# 的共享引用入口，由 Game.Protocol 编译，.NET 测试引用该项目。

默认只输出 `GameNetMessages.cs` 与 `GameNetProtocol.cs` 到 `Unity/Assets/Scripts/Hotfix/Network/Generated/`。Unity 编译该目录；DotNet/Game.Protocol 导入 projitems 编译完全相同的源码，并按自己的目标框架运行 MemoryPack 的 source generator。无需复制 DTO，也不引用 Unity DLL。

开发验证使用 `--artifacts`，额外输出 `protocol.manifest.json`、`protocol.sha256`、`golden-vectors.json` 到仓库根的 `Protocol/Generated/`。这些文件不属于 Unity 资产，不参与正式资源打包或运行时加载。协议指纹始终写入生成的 C# catalog，正式代码生成仍在内存中计算规范清单与 hash；省略辅助文件不会改变协议。默认模式也不构造序列化测试样本。

工具自身不编译磁盘上的旧生成代码，缺少或损坏产物时仍可 generate。工具会先在内存中编译新 DTO 和完整生成源码，并与 MemoryPack serialization-info 对照，再写入结果。临时编译信息在系统临时目录创建，完成后清理。

工具入口为 `Program.cs`，`ProtoProtocolGenerator.cs` 负责解析 Proto 与生成 DTO，`MemoryPackProtocolGenerator.cs` 负责分析实际 MemoryPack 布局并生成 codec/catalog、校验器和向量。这些都是协议生成工具，`DotNet/` 是实际服务端宿主；现已实现 Kestrel local-wss、登录/恢复/注销/Echo，运行配置见 [服务端说明](../DotNet/README.md)。世界服务与生产认证 provider 尚未实现。生成工具引用纯网络 Core；客户端连接实现仅由集成测试工程显式链接，不进入 Core、服务端或生成协议项目。框架与业务程序集关系见 [服务端说明](../DotNet/README.md)。

## 命令

从仓库根运行，SDK 由 `Tools/global.json` 锁定为 10.0.401：

```sh
(cd Tools && dotnet restore NetworkProtocol --locked-mode)
(cd Tools && dotnet run --project NetworkProtocol -- generate)
(cd Tools && dotnet run --project NetworkProtocol -- check)
(cd Tools && dotnet run --project NetworkProtocol -- generate --artifacts)
(cd Tools && dotnet run --project NetworkProtocol -- check --artifacts)
(cd Tools && dotnet test NetworkProtocol.Tests)
(cd DotNet && dotnet build)
```

`check` 比较正式 C#，`check --artifacts` 同时比较三个辅助文件；检查不改文件，缺失、字节差异或 C# 生成目录中的额外 C# 文件均返回非零。generate 不覆写字节未变化的文件，不自动删除多余文件，防止误删 Unity 资产和 `.meta`；移除过时产物时同时处理其 metadata。生成源码、开发验证辅助文件和 NuGet 锁文件应纳入版本管理，CI 使用 `check --artifacts` 检查漏生成。修改协议后在运行 Unity/.NET 向量测试前执行 `generate --artifacts`；正式构建只需默认 generate/check，不依赖外部辅助文件。

`.github/workflows/network.yml` 对网络/协议改动执行锁文件 restore、`check --artifacts`、协议负例/向量与真实 local-wss 测试；独立 job 验证 KCP ACK 对照、已合入修正的源码 hash 与逐点分配失败。CI 不重新生成后掩盖漂移，也不代替 Unity/IL2CPP/设备验收。

## 消息与字段

```proto
syntax = "proto3";
package game.protocol.example;
option csharp_namespace = "Game.Hotfix.Network.Protocol";
import "Common/net_options.proto";

message ExampleRequest {
  uint64 command_id = 1;
  string text = 2 [(game.net.max_utf16) = 64, (game.net.max_utf8) = 256];
  repeated uint32 ids = 3 [(game.net.max_count) = 16];
}
```

字段从 1 连续编号，`= n` 生成 `[MemoryPackOrder(n - 1)]`；声明顺序、注释、空白与文件遍历顺序不改变协议。字段名 snake_case 转 PascalCase，检查转换后名称冲突。每个文件显式声明 package 与 csharp_namespace；跨文件类型通过 import 和完整 Proto 类型名解析。当前 catalog 的 codec/校验器名称要求 DTO 与枚举简单类名全目录唯一，冲突时报错。

| Proto | MemoryPack C# 类型/约束 |
| --- | --- |
| int32/sint32/sfixed32、uint32/fixed32 | int、uint；在线固定宽度，不使用 Proto varint |
| int64/sint64/sfixed64、uint64/fixed64 | long、ulong |
| bool、float、double | 对应 C# 标量 |
| string | string；必须同时声明 max_utf16 和 max_utf8 |
| bytes | byte[]；必须声明 max_count |
| repeated T | T[]；必须声明 max_count；string 元素仍需字符串限额 |
| 顶层 message 类型 | 对应 DTO class；不允许递归 |
| 顶层 enum 类型 | `enum : int`；固定 4 字节小端，支持枚举数组，拒绝未声明值 |
| int32 + scalar="int8"/"int16" | sbyte/short；显式选择 MemoryPack 1/2 字节宽度 |
| uint32 + scalar="uint8"/"uint16" | byte/ushort |

所有引用默认非空：string 初始化为空字符串，数组初始化为空数组，DTO 字段初始化为新对象。缺省/空值语义通过 `(game.net.nullable)=true` 声明；引用数组元素通过 `(game.net.element_nullable)=true` 声明。数值可空禁止，使用显式 bool HasValue + Value 或可空 DTO；不使用 Proto optional 来隐式生成 Nullable<T>。

长度选项是**上限**，不是固定长度要求；sessionId、hash 等必须精确 16/32 字节的业务语义仍由会话处理器校验。字符串上限按 UTF-16 code units 和 UTF-8 bytes 分别计算。

枚举必须先声明零值，成员名称建议直接使用 PascalCase；工具仍检查转换后的名称冲突。禁止别名、Flags、嵌套枚举、可空枚举和枚举宽度收窄。枚举定义随 manifest 纳入 hash；增加结果码也要求两端同步生成。`ResultCode.Success=0` 表示业务成功，其余值只说明业务拒绝；帧头 StatusCode 继续表示系统错误，不能混用。失败响应允许成功载荷保持默认空值，业务处理器仅在成功后使用和校验载荷。

字段注释定义业务约束：成功登录的 accountSessionId/resumeToken 分别恰好 16/32 字节；恢复保留账号会话与 authEpoch 并轮换凭据。grant 成功后校验非零 ID/conv、端点、ticket、PSK 与限额，授权身份由服务端连接绑定提供。UTC 到期时间用于显示/诊断，TTL 以 RPC admission 单调时间起算，不从响应收到时重新续期。上述权限与语义校验属于实际 Hotfix/.NET 处理器，生成的结构验证器不替代它们。

## 路由

```proto
service ExampleService {
  rpc Example(ExampleRequest) returns (ExampleResponse) {
    option (game.net.opcode) = 3000;
    option (game.net.route_kind) = ROUTE_KIND_RPC;
    option (game.net.delivery) = DELIVERY_RELIABLE;
    option (game.net.max_body) = 1024;
    option (game.net.response_max_body) = 1024;
  }
}
```

route_kind 为 RPC、SERVER_PUSH 或 CLIENT_SEND。RPC 必须可靠且声明响应正文上限；Push/Notify 返回 google.protobuf.Empty（需 import google/protobuf/empty.proto），该类型只占位，不编码；它们不能声明非零响应正文上限。通道必须显式选择 DELIVERY_RELIABLE/DELIVERY_UNRELIABLE，不根据 opcode 数字位推断方向，不在运行时自动转换通道。

opcode ≥1000 且在整个目录唯一，生成路由名称也全目录唯一。RPC 响应复用请求 opcode/correlationId；系统 1～999 保留在 Runtime 中，不写进业务 Proto。路由预算必须足够容纳所有声明字段的最大值，同时不超过 V1 通道预算。

世界模式显式选择路由：KCP 使用 `WorldSnapshot` / `MoveInput`（不可靠），WSS 使用 `WorldSnapshotReliable` / `MoveInputReliable`（可靠）；两组分别有独立 opcode，但共用 DTO/codec。实体进入/离开事件始终可靠，借用 WSS 大厅连接时物理连接仍只有大厅所有者负责关闭。

当前小世界示例的入场/恢复响应是完整 AOI 基线，最多 64 个实体；超过返回 TooManyEntities，不截断或让 Runtime 自动分块。后续大世界分块协议按设计第 14 节单独扩展。位置按毫米量化，实体以 entityId + incarnation 区分生命周期；进入/离开事件共享连续 eventVersion，缺号时停止应用并显式恢复。快照是每包最多 16 个实体的独立绝对状态批次，缺席不表示删除；服务器周期覆盖整个 AOI，未知实体/旧 incarnation 丢弃且不缓存。

成功恢复保留 worldSessionId、推进 worldEpoch；输入/快照序号按新 epoch 重置，旧 epoch 消息丢弃。可靠 eventVersion 按世界会话保持连续；响应给出已包含的基线版本，之后只应用更高且连续的成员事件。每个实体分别比较 snapshotSequence，避免乱序的局部批次互相覆盖；u32 序号使用半周期内的环绕差值规则。MoveInput 使用 inputSequence/clientTick 去重和丢弃过期输入，方向轴范围为 [-32767,32767] 且归一化向量长度不超过一，不使用持久经济命令幂等表。实体控制权与移动规则由权威世界处理器校验。

## 校验与版本

拒绝未知选项、类型、漏 import、缺少限额、编号空洞/重复、名称冲突、递归、map、oneof、嵌套 message/enum、枚举别名、streaming RPC 和 Nullable<T>；不默默忽略字段。未使用 import 的解析警告不阻止生成。

生成入站字节形状校验器在 MemoryPack 反序列化前限制长度、UTF-8、深度与节点数量；生成出站对象校验器执行相同字段约束。默认/普通/最大值字节向量由实际 MemoryPack 编码生成，Unity 测试对照 .NET 向量。

Proto 字段号仅映射 Object 成员顺序，**不提供 Protobuf 的字段演进兼容能力**。协议哈希包含路由与实际布局/约束；双方 hash 不一致时拒绝连接。字段更改后重新生成并同步发布两端。IL2CPP、新热更 DTO、实际网络和真机验证仍是后续网络模块验收内容，不能用生成检查替代。
