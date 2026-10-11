# MemoryPack 协议编写与生成

`Protocol/**/*.proto` 是唯一手写协议源。参考 `gameprotomessage` / `protomessage` 的 message、service、选项与 generate/check 组织方式；线上正文为 MemoryPack 1.21.4 UTF-8，帧协议为 [GFNP V1](../Docs/NetworkDesign.md)。Proto 仅是 IDL，不生成 Protobuf DTO，不使用 Protobuf Binary 或 MessagePack。

## 目录与双端使用

- `Common/net_options.proto`：全项目唯一的路由、长度、可空性和收窄整数选项。
- `Session/SessionProto.proto`：登录、授权、世界与 echo DTO。
- `Session/SessionService.proto`：路由；opcode 放在 method 上，DTO 可被多条路由复用。
- `GameFrame.Protocol.projitems`：DotNet 和 .NET 测试的共享源码引用入口。

默认只输出 `GameNetMessages.cs` 与 `GameNetProtocol.cs` 到 `Unity/Assets/Scripts/Hotfix/Network/Generated/`。Unity 编译该目录；DotNet 导入 projitems 编译完全相同的源码，并按自己的目标框架运行 MemoryPack 的 source generator。无需复制 DTO，也不引用 Unity DLL。

开发验证使用 `--artifacts`，额外输出 `protocol.manifest.json`、`protocol.sha256`、`golden-vectors.json` 到仓库根的 `Protocol/Generated/`。这些文件不属于 Unity 资产，不参与正式资源打包或运行时加载。协议指纹始终写入生成的 C# catalog，正式代码生成仍在内存中计算规范清单与 hash；省略辅助文件不会改变协议。默认模式也不构造序列化测试样本。

工具自身不编译磁盘上的旧生成代码，缺少或损坏产物时仍可 generate。工具会先在内存中编译新 DTO 和完整生成源码，并与 MemoryPack serialization-info 对照，再写入结果。临时编译信息在系统临时目录创建，完成后清理。

工具入口为 `Program.cs`，`ProtoProtocolGenerator.cs` 负责解析 Proto 与生成 DTO，`MemoryPackProtocolGenerator.cs` 负责分析实际 MemoryPack 布局并生成 codec/catalog、校验器和向量。这些都是协议生成工具，`DotNet/` 才是服务端宿主；目前服务端入口仍为 Hello World，尚未实现监听、认证与会话处理。客户端连接配置与帧控制代码仅由测试工程链接，不作为生成工具的依赖。

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

字段从 1 连续编号，`= n` 生成 `[MemoryPackOrder(n - 1)]`；声明顺序、注释、空白与文件遍历顺序不改变协议。字段名 snake_case 转 PascalCase，检查转换后名称冲突。每个文件显式声明 package 与 csharp_namespace；跨文件类型通过 import 和完整 Proto 类型名解析。当前 catalog 的 codec 名称要求 DTO 简单类名全目录唯一，冲突时报错。

| Proto | MemoryPack C# 类型/约束 |
| --- | --- |
| int32/sint32/sfixed32、uint32/fixed32 | int、uint；在线固定宽度，不使用 Proto varint |
| int64/sint64/sfixed64、uint64/fixed64 | long、ulong |
| bool、float、double | 对应 C# 标量 |
| string | string；必须同时声明 max_utf16 和 max_utf8 |
| bytes | byte[]；必须声明 max_count |
| repeated T | T[]；必须声明 max_count；string 元素仍需字符串限额 |
| 顶层 message 类型 | 对应 DTO class；不允许递归 |
| int32 + scalar="int8"/"int16" | sbyte/short；显式选择 MemoryPack 1/2 字节宽度 |
| uint32 + scalar="uint8"/"uint16" | byte/ushort |

所有引用默认非空：string 初始化为空字符串，数组初始化为空数组，DTO 字段初始化为新对象。缺省/空值语义通过 `(game.net.nullable)=true` 声明；引用数组元素通过 `(game.net.element_nullable)=true` 声明。数值可空禁止，使用显式 bool HasValue + Value 或可空 DTO；不使用 Proto optional 来隐式生成 Nullable<T>。

长度选项是**上限**，不是固定长度要求；sessionId、hash 等必须精确 16/32 字节的业务语义仍由会话处理器校验。字符串上限按 UTF-16 code units 和 UTF-8 bytes 分别计算。

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

## 校验与版本

拒绝未知选项、类型、漏 import、缺少限额、编号空洞/重复、名称冲突、递归、map、oneof、嵌套 message、业务 enum、streaming RPC 和 Nullable<T>；不默默忽略字段。未使用 import 的解析警告不阻止生成。

生成入站字节形状校验器在 MemoryPack 反序列化前限制长度、UTF-8、深度与节点数量；生成出站对象校验器执行相同字段约束。默认/普通/最大值字节向量由实际 MemoryPack 编码生成，Unity 测试对照 .NET 向量。

Proto 字段号仅映射 Object 成员顺序，**不提供 Protobuf 的字段演进兼容能力**。协议哈希包含路由与实际布局/约束；双方 hash 不一致时拒绝连接。字段更改后重新生成并同步发布两端。IL2CPP、新热更 DTO、实际网络和真机验证仍是后续网络模块验收内容，不能用生成检查替代。
