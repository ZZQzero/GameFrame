# KCP 源码修复与回归验证

当前已将 ACK 聚合修复合入项目 KCP 源码，并提供原生回归测试。UDP、DTLS、gfnet ABI、构建安装脚本和 Unity 工作线程尚未实现。

## 固定基线与修复

- 源码：`Unity/Assets/Plugins/GameFrame/Kcp/ikcp.c`、`ikcp.h`，保留现有路径、CRLF 换行和 `.meta`；`ikcp.c` 在上游基线上增加一处修复，`ikcp.h` 未修改。
- 上游基线：`b1a7a2101dcbb96017681a500d6b82bbe5a88766`。
- 修复来自 [PR #467](https://github.com/skywind3000/kcp/pull/467)，核对的 PR head 为 `7c84862d83761078da0cf8e0a195ed1c5f7d2739`；直接合入 `ikcp.c`，不再保存或重复应用独立补丁。
- 唯一功能修改：更大的 ACK 序号与当前最大 ACK 时间戳相等时，也允许更新 maxack，比较条件由 `> 0` 改为 `>= 0`。保留逐 segment 的旧时间戳检查与每次 input 最多一次 fastack 增量；线协议和原生 ABI 不变。

验证脚本锁定当前源码 SHA-256；仅在临时对照副本中将比较条件还原为 `> 0`，并核对该副本恰好等于上游原版 SHA-256。正式构建直接引用已修复源码，构建清单记录上游基线、已合入的 PR 与实际源码 hash；当前验证脚本仅用于回归，不是插件构建入口。

分配失败的安全补丁仍是后续 P3 任务；此修复不解决 ACK 分配 `abort()`、接收 segment 空指针或发送 assert。不得据此将 KCP 标记为已满足商业内存安全门禁。

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

KCP 和该上游修复按 KCP 的 MIT 许可保留来源，许可证见 [原版 LICENSE](../../../../../../Plugins/GameFrame/Kcp/LICENSE)。
