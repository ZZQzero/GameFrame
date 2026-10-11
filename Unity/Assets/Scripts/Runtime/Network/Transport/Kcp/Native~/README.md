# KCP 源码修复与回归验证

当前已将 ACK 聚合修复合入项目 KCP 源码，并提供原生回归测试、独立分配安全补丁与源码准备工具。UDP、DTLS、gfnet ABI、插件构建安装脚本和 Unity 工作线程尚未实现。

## 固定基线与修复

- 源码：`Unity/Assets/Plugins/GameFrame/Kcp/ikcp.c`、`ikcp.h`，保留现有路径、CRLF 换行和 `.meta`；`ikcp.c` 在上游基线上增加一处修复，`ikcp.h` 未修改。
- 上游基线：`b1a7a2101dcbb96017681a500d6b82bbe5a88766`。
- 修复来自 [PR #467](https://github.com/skywind3000/kcp/pull/467)，核对的 PR head 为 `7c84862d83761078da0cf8e0a195ed1c5f7d2739`；直接合入 `ikcp.c`，不再保存或重复应用独立补丁。
- 唯一功能修改：更大的 ACK 序号与当前最大 ACK 时间戳相等时，也允许更新 maxack，比较条件由 `> 0` 改为 `>= 0`。保留逐 segment 的旧时间戳检查与每次 input 最多一次 fastack 增量；线协议和原生 ABI 不变。

验证脚本锁定当前源码 SHA-256；仅在临时对照副本中将比较条件还原为 `> 0`，并核对该副本恰好等于上游原版 SHA-256。插件构建须从已修复资产开始，再应用下述安全补丁；构建清单记录上游基线、已合入的 PR 与实际源码 hash；当前验证脚本仅用于回归，不是插件构建入口。

ACK 聚合修复本身不解决分配失败；以下安全补丁只应用到构建副本，原 Unity 源码保留已合入 #467 的版本。

## 分配安全补丁与源码准备

`patches/kcp-allocation-safety.patch` 删除两处发送分配成功 assert，让 ACK 扩容返回错误并在 input 中传播为 -4，接收 segment 先检查 NULL；队列偏移宏改用标准 `offsetof`，修复 ASan/UBSan 验证中发现的空指针取成员偏移。create/MTU 的原失败返回无需改动，补丁不改变结构布局或线上格式。

`build/prepare_kcp.py --output <Unity 外不存在的新目录>` 先核对原 c/h、补丁和结果 SHA，再按精确行号与上下文应用；拒绝任何偏移、模糊匹配、未知目标和覆盖既有目录。保留 CRLF 与许可证，写出 `kcp-source-manifest.json`。未来 gfnet 构建必须复用此入口，不能直接编译未应用安全补丁的原资产，也不能用 NDEBUG 掩盖失败。

| 文件 | 准备后的 SHA-256 |
| --- | --- |
| ikcp.c | cc3dae4e8b215f281c0900fd19137c495d80bb779d65f3d430fb821d09f14d29 |
| ikcp.h | 51bb50e747cac6ffd8b29ffd489cf6d300eff231a0a149d6065af353ef38490c |
| 安全补丁 | ee5f2c3fbce4af7a75ea8150051b33eba7b0461ecda468d2ae341f925917f89d |

完整分配点审计：

| 调用路径 | 失败结果与状态 |
| --- | --- |
| create 控制块/初始 buffer | NULL；第二点失败释放控制块 |
| setmtu 替换 buffer | -2；保留旧 buffer/MTU |
| send 普通 fragment、stream 替换尾 segment | -2；此前 segment 或已合并数据可能保留 |
| input ACK 首次分配/扩容 | -4；保留旧 ACK 数组；此前数据可能已输入 |
| input 接收 segment | -4；该 segment 不入队，但 ACK 已记录 |

所有失败实例只能由未来包装层锁定 fatal 并销毁；不能继续 input/update、发送已经记录的 ACK，或重试整个应用帧。KCP 补丁不新增业务回滚。动态分配仅来自上述控制块/buffer、ACK 数组和共用 segment allocator；send/input 的部分修改在释放时均可回收。

从仓库根运行：

```sh
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/tests/verify_allocation_safety.py
```

默认运行 assert、NDEBUG 与 ASan/UBSan；sanitizer 错误使进程和验证失败。仅工具链不支持 sanitizer 时显式 `--no-sanitizers`，结果不代表 sanitizer 验收。每种模式覆盖 25 个成功/逐点失败场景：含多 fragment/stream 的中间失败、零长 send、多段 input、ACK 扩容保留原数组；allocator 逐项验证存活字节/对象归零。补丁上下文/偏移/长度被改动必须拒绝，已有输出不覆盖。同时重跑真实 KCP 端点的 ACK、时钟回绕与固定 tick 回归；所有产物位于系统临时目录并自动清理。

## 运行回归

从仓库根目录执行，需要 Python 3 和支持下列参数的 C/C++ 编译器；macOS/Linux 默认使用 `cc`/`c++`：

```sh
python3 Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/tests/verify_fastack.py
```

可以用 `--cc clang --cxx clang++` 显式选择编译器。脚本检查当前源码 SHA-256、还原临时原版对照，分别启用/禁用 assert 编译运行；所有编译产物保存在系统临时目录，结束后自动清理，不写入 Unity 缓存或安装原生库。

每个可执行文件覆盖 8 个 ACK 场景：PR 参数/项目参数 × 升序/逆序 ACK × 普通/回绕时钟。两个真实 KCP 端点发送满 MSS 数据，丢弃一个完整 datagram，再输入两批接收端实际生成的 ACK。项目参数使用 MTU=1150、窗口=128、resend=2、nc=0，并设置成熟连接的 cwnd；不将该窗口称为建连初值。

原版升序场景在 30ms 只收到 1/7 条消息是预期对照，脚本必须复现它才能通过；修补版所有场景应收到 7/7 且未触发 RTO。断言不包裹 send/input 等副作用，`NDEBUG` 构建仍执行完整验证。

另验证一次延迟 update 后 `check=200ms`、`update` 未 flush、再次 `check=200ms` 的边界，并确认下一次固定 10ms tick 能推进重传。它验证原生调度条件，不代表尚未实现的 Unity 工作线程已通过测试。

## 工作线程规则

第一版按每连接单调时间固定 10ms 推进 KCP update；发送、接收、DTLS timer 与取消可以提前唤醒工作线程，但不重置既定 tick。晚到 tick 只用当前时间推进一次，再安排下一次，不补跑遗漏 tick。

不将 `ikcp_check()` 返回当前时间视为在同一轮无限重入 update 的要求，不采用 PR #48 修改 check 状态。大量连接的期限调度优化留待实测；现阶段遵守每轮输入/输出预算和连接轮转，不增加通用调度框架。

## 验证边界与许可

这是原生确定性模拟，覆盖 ACK 聚合与调度边界，不含 DTLS/socket、Unity、IL2CPP、移动设备或真实弱网验证。发布前仍须执行设计文档中的完整门禁。

2026-10-11 在 macOS arm64 执行上述入口：修复版 ACK 场景 16/16 通过，原版对照复现升序 ACK 下 1/7 的交付结果，调度边界 4/4 通过。编译与运行均使用系统临时目录，验证后已清理；未生成或安装原生插件。

同日在 macOS arm64 执行分配安全入口：精确补丁防漂移检查通过，assert/NDEBUG/ASan+UBSan 共 75/75 分配场景通过，准备后源码的 ACK 场景 24/24、调度边界 3/3 通过，无 sanitizer 错误；原资产未改。该证据不包含 DTLS 分配、gfnet fatal 状态、socket、完整 ABI 或插件安装验收。上游未启用的调试日志仍使用 vsprintf，编译器会报弃用警告；不将无日志的这些回归宣称为日志路径审计。

KCP 和该上游修复按 KCP 的 MIT 许可保留来源，许可证见 [原版 LICENSE](../../../../../../Plugins/GameFrame/Kcp/LICENSE)。
