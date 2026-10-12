# C KCP / DTLS 原生插件

已实现 C KCP、mbedTLS DTLS 1.2 PSK、固定 gfnet ABI、配额分配器与 listener。C# 负责 UDP；Unity 客户端一个宿主 worker，.NET listener 一个 owner。实现方案见 [NetworkNativeDesign.md](../../../../../../../../Docs/NetworkNativeDesign.md)，精确布局/返回值见 [ABI.md](ABI.md)，实际验证和剩余门禁见 [NetworkImplementation.md](../../../../../../../../Docs/NetworkImplementation.md)。

## 构建与安装

依赖锁定 mbedTLS 4.2.0（stable，TF-PSA-Crypto 1.2.0；上游 LTS 为 4.1）；下载是显式准备步骤，普通构建不下载或升级。准备器校验官方 archive SHA，将归档中的相对链接统一展开为普通文件，Windows 无需管理员或 Developer Mode；树 hash 在各宿主使用相同文件布局。TLS 与 PSA 最小配置分离，保留 PSA 默认缓冲复制。随机数通过 PSA external RNG 使用平台系统密码级接口，失败清零输出并明确终止创建/握手。所有中间目录在 Unity 外；下面以仓库根目录、PATH 中有 CMake 的 macOS 为例：

```sh
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/build/prepare_mbedtls.py --output Tools/Artifacts/NetworkNative/mbedtls-4.2.0
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/build/build.py --platform macos --arch universal --configuration Release --output Tools/Artifacts/NetworkNative/macos-release --mbedtls Tools/Artifacts/NetworkNative/mbedtls-4.2.0 --tests
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/build/install.py Tools/Artifacts/NetworkNative/macos-release
```

用 `--cmake /absolute/path/cmake` 指定既有 CMake。Windows 在 MSVC 构建机使用 `--platform windows --arch x86_64`；macOS 可像 Sqlite 一样额外指定 `--llvm-mingw /path/to/llvm-mingw-ucrt` 交叉构建 x64 DLL；Linux 原生构建使用匹配宿主的 arm64/x86_64。Android：`--platform android --arch arm64 --ndk /path/to/ndk`；iOS：`--platform ios --arch arm64`；模拟器：`--platform ios-simulator --arch arm64|x86_64`。每个目标使用独立 output。`--dry-run` 只输出计划，`--tests` 在匹配宿主执行，Windows 交叉构建只编译回归 exe，清单明确记录 `built=true, executed=false`；`--sanitizers --tests` 仅生成验证库，安装器拒绝 sanitizer 产物。

Windows 构建和安装均检查 x64 PE、gfnet.h 声明的完整导出与系统 DLL 依赖；MSVC 使用静态 CRT，不引入额外运行时 DLL。安装器验证当前 KCP 源码/依赖锁、构建清单 build_id 和产物 hash，原子替换文件并保留 GUID；不预加载 KCP。安装目录为 `Assets/Plugins/GameFrame/Native/Kcp`，不影响 Sqlite/Backup。模拟器 archive 作为独立产物默认禁用，构建模拟器时显式选择模拟器并禁用 device archive，避免重复链接。iOS 发布 archive 单独合并静态依赖，不覆盖 CMake 原始 target，可安全重复构建。

.NET 发布使用同一安装器 `--server-output <服务器输出目录>`，将与 RID 匹配的库放在可执行程序旁。修改已被 Unity 加载的二进制后须重启 Editor；缺库/架构/ABI 错误明确失败，WSS-only 不加载 KCP。许可证、artifact.json 和 build-manifest.json 随产物发布。

## 固定基线与修复

- 源码：`Unity/Assets/Plugins/GameFrame/Kcp/ikcp.c`、`ikcp.h`，保留现有路径、CRLF 换行和 `.meta`；c/h 已包含下述修正，作为构建的唯一 KCP 源码。
- 上游基线：`b1a7a2101dcbb96017681a500d6b82bbe5a88766`。
- 修复来自 [PR #467](https://github.com/skywind3000/kcp/pull/467)，核对的 PR head 为 `7c84862d83761078da0cf8e0a195ed1c5f7d2739`；直接合入 `ikcp.c`，不再保存或重复应用独立补丁。
- ACK 修复：更大的 ACK 序号与当前最大 ACK 时间戳相等时，也允许更新 maxack，比较条件由 `> 0` 改为 `>= 0`。保留逐 segment 的旧时间戳检查与每次 input 最多一次 fastack 增量；线协议和原生 ABI 不变。

验证脚本锁定当前源码 SHA-256；仅在临时对照副本中将比较条件还原为 `> 0`，双方保留相同分配安全修正，只比较 ACK 行为。对照副本 hash 锁定为 `cbf9c7faa39a271651950ae4f73f788e6127d989a1c502e5f38182940d63530c`，不将其称为未修改的上游源码。构建清单记录上游基线、已合入的 PR、本地修正与实际源码 hash；当前验证脚本仅用于回归，不是插件构建入口。

ACK 聚合修复本身不解决分配失败；下述分配安全修正独立于 #467，现已同样写入 c/h，不再维护第二份补丁文件。

## 分配安全修正与源码准备

`ikcp.c` 删除两处发送分配成功 assert，让 ACK 扩容返回错误并在 input 中传播为 -4，接收 segment 先检查 NULL；`ikcp.h` 的队列偏移宏改用标准 `offsetof`，修复 ASan/UBSan 验证中发现的空指针取成员偏移。create/MTU 的原失败返回无需改动，这些修正不改变结构布局或线上格式。

`build/prepare_kcp.py --output <Unity 外不存在的新目录>` 核对已合入修正的 c/h SHA 后逐字节复制；拒绝源码漂移、Unity 内输出和覆盖既有目录。保留 CRLF 与许可证，写出 `kcp-source-manifest.json`，记录上游提交、#467、本地修正与源码 hash。gfnet 构建复用此入口，不再打补丁，也不能用 NDEBUG 掩盖失败。

| 文件 | 当前源码与构建副本的 SHA-256 |
| --- | --- |
| ikcp.c | cc3dae4e8b215f281c0900fd19137c495d80bb779d65f3d430fb821d09f14d29 |
| ikcp.h | 51bb50e747cac6ffd8b29ffd489cf6d300eff231a0a149d6065af353ef38490c |

完整分配点审计：

| 调用路径 | 失败结果与状态 |
| --- | --- |
| create 控制块/初始 buffer | NULL；第二点失败释放控制块 |
| setmtu 替换 buffer | -2；保留旧 buffer/MTU |
| send 普通 fragment、stream 替换尾 segment | -2；此前 segment 或已合并数据可能保留 |
| input ACK 首次分配/扩容 | -4；保留旧 ACK 数组；此前数据可能已输入 |
| input 接收 segment | -4；该 segment 不入队，但 ACK 已记录 |

所有失败实例只能由gfnet 包装层锁定 Fatal 并销毁；不能继续 input/update、发送已经记录的 ACK，或重试整个应用帧。这些修正不新增业务回滚。动态分配仅来自上述控制块/buffer、ACK 数组和共用 segment allocator；send/input 的部分修改在释放时均可回收。

从仓库根运行：

```sh
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/tests/verify_allocation_safety.py
```

默认运行 assert、NDEBUG 与 ASan/UBSan；sanitizer 错误使进程和验证失败。仅工具链不支持 sanitizer 时显式 `--no-sanitizers`，结果不代表 sanitizer 验收。每种模式覆盖 25 个成功/逐点失败场景：含多 fragment/stream 的中间失败、零长 send、多段 input、ACK 扩容保留原数组；allocator 逐项验证存活字节/对象归零。c/h 源码漂移必须拒绝，已有输出不覆盖，复制后源码逐字节一致，Unity 元数据保持原值。同时重跑真实 KCP 端点的 ACK、时钟回绕与固定 tick 回归；所有产物位于系统临时目录并自动清理。

## 运行回归

从仓库根目录执行，需要 Python 3 和支持下列参数的 C/C++ 编译器；macOS/Linux 默认使用 `cc`/`c++`：

```sh
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/tests/verify_fastack.py
```

可以用 `--cc clang --cxx clang++` 显式选择编译器。脚本检查当前源码 SHA-256、还原临时旧 ACK 比较条件对照，分别启用/禁用 assert 编译运行；所有编译产物保存在系统临时目录，结束后自动清理，不写入 Unity 缓存或安装原生库。

每个可执行文件覆盖 8 个 ACK 场景：PR 参数/项目参数 × 升序/逆序 ACK × 普通/回绕时钟。两个真实 KCP 端点发送满 MSS 数据，丢弃一个完整 datagram，再输入两批接收端实际生成的 ACK。项目参数使用 MTU=1150、窗口=128、resend=2、nc=0，并设置成熟连接的 cwnd；不将该窗口称为建连初值。

旧 ACK 比较条件的升序场景在 30ms 只收到 1/7 条消息是预期对照，脚本必须复现它才能通过；当前源码所有场景应收到 7/7 且未触发 RTO。断言不包裹 send/input 等副作用，`NDEBUG` 构建仍执行完整验证。

另验证一次延迟 update 后 `check=200ms`、`update` 未 flush、再次 `check=200ms` 的边界，并确认下一次固定 10ms tick 能推进重传。它验证原生调度条件，只验证原生调度条件；完整 worker 实联证据见实施记录。

## 工作线程规则

第一版由 native 按每连接单调时间固定 10ms 推进 KCP update；托管发送/关闭命令可提前唤醒，当前托管 deadline、UDP/DTLS timer 在下一 owner 轮检查；最近 deadline 的精细等待调度尚未实现，不重置既定 tick。提前 tick 不额外 flush；晚到 tick 只用当前时间推进一次，再安排下一次，不补跑遗漏 tick。具体 worker/队列规则以底层实施方案为准，现有 KcpWorker 按上述规则驱动。

不将 `ikcp_check()` 返回当前时间视为在同一轮无限重入 update 的要求，不采用 PR #48 修改 check 状态。大量连接的期限调度优化留待实测；现阶段遵守每轮输入/输出预算和连接轮转，不增加通用调度框架。

## 验证边界与许可

上述独立KCP脚本是原生确定性模拟，覆盖 ACK 聚合与调度边界；其结果不含 DTLS/socket、Unity、IL2CPP、移动设备或真实弱网验证。完整gfnet和实联的最新证据见本节末尾与实施记录。发布前仍须执行设计文档中的完整门禁。

2026-10-11 在 macOS arm64 执行上述入口：修复版 ACK 场景 16/16 通过，原版对照复现升序 ACK 下 1/7 的交付结果，调度边界 4/4 通过。编译与运行均使用系统临时目录，验证后已清理；未生成或安装原生插件。

同日在 macOS arm64 执行分配安全入口的历史验证：独立补丁模式下，assert/NDEBUG/ASan+UBSan 共 75/75 分配场景、准备后 ACK 24/24、调度边界 3/3 通过。后续按用户要求将这些修正直接合入 c/h，删除补丁与应用逻辑，改为锁定源码复制；直接合入后的验证需以新的运行结果为准。该证据不包含 DTLS 分配、gfnet fatal 状态、socket、完整 ABI 或插件安装验收。上游未启用的调试日志仍使用 vsprintf，编译器会报弃用警告；不将无日志的这些回归宣称为日志路径审计。

直接合入后再次在 macOS arm64 运行两个入口：源码 hash/复制一致性、c/h 漂移拒绝、禁止 Unity 内输出及禁止覆盖检查通过；分配安全 75/75、ACK 24/24、tick 3/3 通过，ASan/UBSan 无错误。独立 ACK 回归确认旧比较条件对照仍为 1/7，当前源码 ACK 16/16、tick 4/4 通过。c/h 保留 CRLF，两个 `.meta` 与修改前一致，临时产物已清理；未构建或安装 gfnet 插件。

KCP 和该上游修复按 KCP 的 MIT 许可保留来源，许可证见 [原版 LICENSE](../../../../../../Plugins/GameFrame/Kcp/LICENSE)。

2026-10-12：gfnet DTLS/KCP/listener host harness 1303 个 REQUIRE 检查通过，ASan/UBSan 无错误、释放后 native bytes=0；Unity Mono 实际 UDP Hello/Echo/释放用例通过。平台构建与完整验证的不同证据仍以实施记录为准。

当前ABI2增加peek_frame的原始enqueued_ms输出，全部结构offset/size在C/C#两侧验证；必须同时更新托管和插件，旧ABI拒绝。可靠/不可靠接收按16/4消费轮转，peek/header/BufferTooSmall不推进；普通Closed后仍可读已收到完整帧，release之后句柄失效。2026-10-12本轮ASan/UBSan harness2294次检查通过，包含实际DTLS公平性/回绕/close_notify；最新平台和Unity证据见实施记录。

4.2.0 升级使用 mandatory PSA API：cookie setup 不再传 DRBG，ticket 摘要通过 psa_hash_compute，错误保留单一 SSL/PSA 原值；ABI2 不变。PSA 全局一次初始化，静态1024槽共享且受锁保护，动态密钥/操作缓冲按每次调用的 quota 计账。Windows 分别使用 MSVC 的 __declspec(thread) 和 MinGW 的 C11 _Thread_local，禁止编译器忽略 TLS 声明。最新验证与平台限制见实施记录中的升级条目，旧2294/1303检查数量为3.6.7历史结果。
