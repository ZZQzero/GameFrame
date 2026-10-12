# 网络模块全量复核记录（2026-10-12）

本轮用户范围为 NetworkDesign / C KCP DTLS 底层方案与网络模块完整实现。世界会话业务与 WebGL WSS 按已确认范围延后。以下逐文件清单覆盖网络维护源码、仓库内 KCP、生成结果、协议输入/工具、服务端、调用方、测试及构建安装入口；搜索只用于建立清单和交叉核验，实现逐项完整阅读。

## 调用与资源归属

- GameRuntime → NetworkRuntime → NetworkClient → 每次连接独立 ConnectionContext；GameRuntime root 驱动主线程 Pump，Context 持有 pending、帧队列和 IO 生命周期。
- Context → IFrameTransport → WebSocketTransport / KcpTransport；KcpWorker 独占客户端 native handle，C# socket → gfnet → mbedTLS / KCP。DTO 不进入 C；业务完成由主线程派发。
- DotNet Program → LocalWssService / LocalKcpService；WSS ServerConnection 分离收、发、业务 owner；KcpListenerHost 的 owner 独占 socket/listener/child native handle，异步 handler 只处理拥有的托管帧和授权连接身份。
- Proto → ProtoProtocolGenerator → MemoryPackProtocolGenerator → 两个生成 C# 文件；Unity 与 DotNet 编译同源 codec/catalog，manifest/hash/vector 仅供离线校验。
- build.py → 固定依赖/源码校验 → CMake → 平台产物与清单 → install.py → Unity importer / DotNet 发布目录。替换新 ABI 后必须新进程验证。

## 已阅读的文件

下表的“已读”仅表示源码/配置审查，编译和执行证据另列。修改后的调度、关闭、ABI 和计账方法重新对照调用链检查。

| 文件 | 源码状态 |
| --- | --- |
| `.github/workflows/network.yml` | 已读 |
| `Docs/ErrorContract.md` | 已读 |
| `Docs/Launch.md` | 已读 |
| `Docs/Network.md` | 已读 |
| `Docs/NetworkDesign.md` | 已读 |
| `Docs/NetworkImplementation.md` | 已读 |
| `Docs/NetworkNativeDesign.md` | 已读 |
| `Docs/Scope.md` | 已读 |
| `DotNet/Game.Server/AssemblyInfo.cs` | 已读 |
| `DotNet/Game.Server/Game.Server.csproj` | 已读 |
| `DotNet/Game.Server/Login/AccountSessions.cs` | 已读 |
| `DotNet/GameFrame.Server/KcpListenerHost.cs` | 已读 |
| `DotNet/Game.Server/LocalKcpService.cs` | 已读 |
| `DotNet/GameFrame.Server/WssServer.cs` | 已读 |
| `DotNet/GameFrame.Server/ServerConnection.cs` | 已读 |
| `DotNet/Game.Server/Login/LoginOptions.cs` | 已读 |
| `DotNet/Game.Server/Program.cs` | 已读 |
| `DotNet/Game.Server/LobbyApplication.cs` | 已读 |
| `DotNet/Game.Server/Login/LoginHandlers.cs` | 已读 |
| `DotNet/Game.Server/Login/LoginSession.cs` | 已读 |
| `DotNet/Game.Server/LocalEnvironment.cs` | 已读 |
| `DotNet/Game.Server/LocalWssService.cs` | 已读 |
| `DotNet/README.md` | 已读 |
| `DotNet/global.json` | 已读 |
| `DotNet/Game.Server/packages.lock.json` | 已读 |
| `Protocol/Common/ResultProto.proto` | 已读 |
| `Protocol/Common/net_options.proto` | 已读 |
| `Protocol/Diagnostics/DiagnosticsProto.proto` | 已读 |
| `Protocol/Diagnostics/DiagnosticsService.proto` | 已读 |
| `Protocol/GameFrame.Protocol.projitems` | 已读 |
| `Protocol/Generated/golden-vectors.json` | 已读 |
| `Protocol/Generated/protocol.manifest.json` | 已读 |
| `Protocol/Generated/protocol.sha256` | 已读 |
| `Protocol/README.md` | 已读 |
| `Protocol/Session/SessionProto.proto` | 已读 |
| `Protocol/Session/SessionService.proto` | 已读 |
| `Protocol/World/WorldProto.proto` | 已读 |
| `Protocol/World/WorldService.proto` | 已读 |
| `Tools/NetworkProtocol.Tests/GeneratorTests.cs` | 已读 |
| `Tools/NetworkProtocol.Tests/NetworkOptionsTests.cs` | 已读 |
| `Tools/NetworkProtocol.Tests/NetworkProtocol.Tests.csproj` | 已读 |
| `Tools/NetworkProtocol.Tests/ProtoGenerationTests.cs` | 已读 |
| `Tools/NetworkProtocol.Tests/packages.lock.json` | 已读 |
| `Tools/NetworkProtocol/AssemblyInfo.cs` | 已读 |
| `Tools/NetworkProtocol/MemoryPackProtocolGenerator.cs` | 已读 |
| `Tools/NetworkProtocol/NetworkProtocol.csproj` | 已读 |
| `Tools/NetworkProtocol/Program.cs` | 已读 |
| `Tools/NetworkProtocol/ProtoProtocolGenerator.cs` | 已读 |
| `Tools/NetworkProtocol/ProtocolRouteDefinition.cs` | 已读 |
| `Tools/NetworkProtocol/ProtocolSamples.cs` | 已读 |
| `Tools/NetworkProtocol/packages.lock.json` | 已读 |
| `Tools/NetworkServer.Tests/ConnectionRaceTests.cs` | 已读 |
| `Tools/NetworkServer.Tests/KcpLoopbackTests.cs` | 已读 |
| `Tools/NetworkServer.Tests/KcpSourceBudgetTests.cs` | 已读 |
| `Tools/NetworkServer.Tests/NetworkServer.Tests.csproj` | 已读 |
| `Tools/NetworkServer.Tests/ServerHeartbeatTests.cs` | 已读 |
| `Tools/NetworkServer.Tests/WssServerTests.cs` | 已读 |
| `Tools/NetworkServer.Tests/packages.lock.json` | 已读 |
| `Tools/README.md` | 已读 |
| `Tools/global.json` | 已读 |
| `Native/Kcp/third_party/kcp/LICENSE` | 已读 |
| `Native/Kcp/third_party/kcp/ikcp.c` | 已读 |
| `Native/Kcp/third_party/kcp/ikcp.h` | 已读 |
| `Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetMessages.cs` | 已读 |
| `Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetProtocol.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/AssemblyInfo.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Lifecycle/GameRuntime.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Lifecycle/GameRuntimeConfig.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Connection/ConnectionContext.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Connection/PendingDeadlineHeap.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Messaging/NetProtocolCatalog.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/NetworkClient.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/NetworkConnectOptions.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/NetworkDiagnostics.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/NetworkException.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/NetworkOptions.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/NetworkRuntime.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Protocol/ControlCodec.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Protocol/FrameCodec.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetProtocolContracts.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetWireReader.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/IFrameTransport.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpNative.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpTransport.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpWorker.cs` | 已读 |
| `Native/Kcp/ABI.md` | 已读 |
| `Native/Kcp/CMakeLists.txt` | 已读 |
| `Native/Kcp/README.md` | 已读 |
| `Native/Kcp/build/build.py` | 已读 |
| `Native/Kcp/build/install.py` | 已读 |
| `Native/Kcp/build/prepare_kcp.py` | 已读 |
| `Native/Kcp/build/prepare_mbedtls.py` | 已读 |
| `Native/Kcp/build/verify_windows.py` | 已读 |
| `Native/Kcp/build/windows-llvm-mingw.cmake` | 已读 |
| `Native/Kcp/config/mbedtls_config.h` | 已读 |
| `Native/Kcp/include/gfnet.h` | 已读 |
| `Native/Kcp/src/gfnet_connection.c` | 已读 |
| `Native/Kcp/src/gfnet_internal.h` | 已读 |
| `Native/Kcp/src/gfnet_listener.c` | 已读 |
| `Native/Kcp/src/gfnet_memory.c` | 已读 |
| `Native/Kcp/tests/allocation_safety.c` | 已读 |
| `Native/Kcp/tests/fastack_regression.cpp` | 已读 |
| `Native/Kcp/tests/gfnet_regression.c` | 已读 |
| `Native/Kcp/tests/verify_allocation_safety.py` | 已读 |
| `Native/Kcp/tests/verify_fastack.py` | 已读 |
| `Native/Kcp/third_party/mbedtls.lock.json` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/OwnedFrame.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketFrameTransport.cs` | 已读 |
| `Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketTransport.cs` | 已读 |
| `Unity/Assets/Scripts/Tests/EditMode/Network/NetworkBufferTests.cs` | 已读 |
| `Unity/Assets/Scripts/Tests/EditMode/Network/NetworkMemoryPackTests.cs` | 已读 |
| `Unity/Assets/Scripts/Tests/EditMode/Network/NetworkProtocolTests.cs` | 已读 |
| `Unity/Assets/Scripts/Tests/PlayMode/NetworkKcpLoopbackTests.cs` | 已读 |
| `Unity/Assets/Scripts/Tests/PlayMode/NetworkLifecycleTests.cs` | 已读 |

Unity asmdef/asmref、相关 .meta 与原生安装清单另按程序集边界、GUID、平台选择和实际产物核验，不把二进制列作可阅读的维护源码。

## 依赖审查边界与尚未验收的契约

- 仓库内 ikcp.c / ikcp.h 全文及所有本地修正已覆盖。
- mbedTLS 3.6.7 的锁、裁剪配置、构建、allocator/BIO/timer/PSK 接入和调用错误边界已覆盖；整个上游密码算法库未逐行安全审计。NuGet MemoryPack、protobuf-net.Reflection、Roslyn 同样核对锁与调用/生成结果，未对全部上游库源码做安全审计。不能将本轮报告作为整个第三方依赖栈的完整安全审计。
- 帧与 native 精确容量有界，pending/帧记录仍采用保守 reservation。CLR/Mono/IL2CPP 常驻容器、任务与取消注册的完整成本核验未完成；完整预算验收仍缺平台测量。
- Connect/Hello/RPC 分阶段及分位指标、worker/Pump 用时，mbedTLS 内部 bad-MAC/replay/cookie 分项指标尚未完整实现。
- 新 ABI 的五个平台产物已重建安装，并校验源码、构建清单、hash和GUID；Unity新进程加载和实际UDP通过。平台实跑范围见下表。
- Windows 原生执行、远程 CI、IL2CPP 最终链接/设备、IPv6/后台/弱网、容量和长时 soak 尚未执行；不能由 host 回归代替。

## 方案选择与已修改问题

| 问题 | 比较与采用方案 |
| --- | --- |
| 正常关闭丢弃已收消息 | 采用现有 receive/dispatch owner 排空和关闭标记；native Closed 后只读已有完整帧，release 后失效。避免另建关闭补偿缓存 |
| 等待WS关闭确认仍接受/发送排队请求 | EOF立即停止admission，丢弃未移交帧；在途Send因已确认EOF失败不覆盖此前保留响应，控制关闭确认仍由发送owner完成 |
| 不可靠队列挡住可靠发送 | 分离各通道 FIFO，控制/可靠优先；原生未接受时保留队首和所有权，单次移交允许其他可发送通道继续 |
| 不可靠 native 接收饥饿、队龄重置 | native / managed 按 16可靠/4不可靠消费轮转；peek/header/短缓冲不推进。ABI2 透传原始 born，跨 uint32 回绕恢复队龄 |
| 可靠 admission 过晚失败 | 在 correlation/pending 分配前结合队列、token 和 native window 估计等待；仍在真正移交前检查取消/期限 |
| socket 阻塞时继续入 native、每轮重复输出配额 | 保留 in-flight UDP 原始 birth，暂停新业务追加；每轮首尾 flush 共用 256 包预算 |
| ABI 只有部分 offset 验证、错误分类过宽 | ABI2 C/C# 全公开字段守卫；分配失败先于其他 DTLS 分类，内部故障与远端协议错误分开并保留首因 |
| 应用 handler 缺授权连接身份 | 直接将 id/generation/grantId 作为不可变参数传入现有 handler；世界业务接入与生命周期通知仍需业务 owner 的实际契约 |
| grant 排队延长 TTL / 临时密钥清理 | deadline 从注册 admission 起算，owner 只用剩余 TTL；转换和登记异常也清零已分配凭据 |
| 临时握手内部失败被升级为共享监听器故障 | feed 返回值包含临时 child create/tick 错误，不按 InternalError 类别停止共享 socket；子连接终态走原生事件，tick/poll 仍传播 listener 自身失败 |
| 文档承诺与实际指标不一致 | 增补现有可观测计数/RTT/峰值，明确不可观测项；同步固定 10ms 驱动承诺，保留精细调度的实测门槛 |

## 本轮实际验证

| 层级 | 实际证据 | 限制 |
| --- | --- | --- |
| 原生macOS | arm64 ASan/UBSan harness2294次断言通过，live bytes=0；universal Release ctest通过 | 不等于2294个独立场景，完整分配/容量矩阵仍见实施记录 |
| 独立KCP | 分配成功/失败75/75；ACK16/16、tick4/4与准备后回归通过 | 无socket/平台设备 |
| .NET协议 | 178/178、check --artifacts通过 | 不替代Unity/IL2CPP |
| .NET网络 | 最终全套59/59通过、0跳过，含真实UDP/WSS、可控EOF/在途Send失败、优先级及错误凭据握手隔离回归 | host平台macOS，无Windows执行；未逐项注入所有DTLS内部错误 |
| 原生产物 | ABI2五个平台构建安装；源码/build_id/binary hash核验，原meta逐字节不变；Windows26导出与系统依赖核验 | Windows回归exe仅编译；Android/iOS/simulator未设备执行 |
| Unity | 新Editor进程加载ABI2；网络EditMode143/143、生命周期11/11、实际UDP Hello/Echo/释放1/1；StandaloneOSX Player脚本43程序集通过 | 无原生IL2CPP最终链接、设备、弱网/soak |

保留Launch.unity且未播放、无场景修改。恢复EditorSettings原文件与内存选项；临时重连脚本、凭据、验证日志与Player脚本输出结束后清理。构建目录保留在忽略的Tools/Artifacts内。历史结果在NetworkImplementation的历史节保留。

## 后续依赖升级记录

2026-10-12 用户要求升级到 mbedTLS 4.2.0。此前109文件全量网络审查的源码/测试证据对应3.6.7；升级改动和重新执行的原生、.NET、Unity证据独立记录于 [NetworkImplementation.md](NetworkImplementation.md) 的“mbedTLS 4.2.0 升级”。本次针对依赖迁移读取官方4.0迁移说明、4.2/TF1.2 API与实际涉及的构建/配置/native生命周期代码，修正错误码、PSA内存归属、系统随机数和MinGW TLS声明；官方源码内容/哈希核验不等于逐文件审查全部第三方实现，也不等于平台实跑。原全量审查清单继续保留，不以依赖升级替代其中未完成的平台/诊断门禁。

原生目录迁移说明：本记录中的源码清单与链接已更新为公共 `Native/Kcp` 路径；先前审查和运行证据保留其原有时间与范围，迁移后的验证另见 NetworkImplementation.md。

服务端程序集拆分后，清单中的服务端路径已更新到维护位置；历史审查结论与当时验证不改写。现行边界、接口与本轮验证见 [NetworkServerReview](NetworkServerReview.md) 第 8 节。
