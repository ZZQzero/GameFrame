# gameframe_kcp ABI 1

唯一函数声明是 [include/gfnet.h](include/gfnet.h)，C# 声明位于相邻 `KcpNative.cs`。仅支持 64-bit，Cdecl，默认自然对齐 8；固定宽度整数，禁止 C long/bool/隐式字符串。库名 `gameframe_kcp`，iOS 为 `__Internal`；公开导出仅 `gfnet_*`。不把内部 KCP/mbedTLS 结构暴露给托管。

## 结构布局

下表偏移均为 bytes，数组内元素连续；所有保留位为 0，所有带 size/version 的输入或输出结构先填实际 size 和 version=1。错误布局返回 InvalidArgument，不按旧长度静默兼容。

| 结构 / size | 字段偏移 |
| --- | --- |
| config / 64 | size0 version4 role8 conv12 native_limit16 reliable_limit20 unreliable_limit24 reserved28 identity_ptr32 identity_length40 reserved_identity44 psk_ptr48 psk_length56 reserved_psk60 |
| stats / 168 | size0 version4 state8 capabilities12 fatal_code16 native_error20 owned_bytes24 peak_bytes32 allocations40 udp_received_bytes48 udp_polled_bytes56 udp_received_packets64 udp_polled_packets72 raw_dropped80 unreliable_dropped88 input_count96 output_count100 output_bytes104 output_oldest_ms108 waitsnd112 srtt116 rto120 snd_wnd124 rcv_wnd128 remote_wnd132 plaintext_count136 plaintext_bytes140 pending_reliable_bytes144 inflight_reliable_bytes152 timeout_retransmits160 |
| peer / 24 | address_family0 address[16]4 port20 reserved22 |
| listener_config / 32 | size0 version4 native_limit8 connection_limit12 max_connections16 max_handshakes20 max_grants24 reserved28 |
| grant / 104 | size0 version4 conv8 remaining_ttl_ms12 grant_id16 identity[16]24 psk[32]40 ticket_digest[32]72 |
| event / 72 | size0 version4 kind8 reason12 connection_id16 generation24 grant_id32 connection_ptr40 peer48 |
| udp_metadata / 64 | size0 version4 enqueued_ms8 reserved12 connection_id16 generation24 peer32 reserved_tail[2]56 |

peer family 为 4/6；address 为网络序字节，IPv4 使用前 4 bytes，后 12 bytes 必须为 0；port 为主机序。初版拒绝 IPv6 link-local/scoped 地址。托管 `Marshal.SizeOf/OffsetOf` 与 C 静态断言共同校验布局。

Config create 同步复制 16-byte identity、32-byte PSK。role=1 client / 2 standalone server；standalone server 用于底层 harness，实际多客户端服务使用 listener。全部输入/输出 byte buffer 只在调用期间有效。grant 注册复制全部数据；ticket_digest 为 SHA-256，原始票据不保存到 native grant 表。

## 成功与错误返回

- create/feed/tick/send/stats/state/destroy 及 listener 注册、绑定、关闭、release、destroy：成功 0。
- peek_frame：有帧 1，并输出 channel=1 reliable / 2 unreliable 和完整 GFNP 长度；无帧 0。
- peek_frame_header：有帧返回 32，仅复制固定帧头，不消费；无帧 0。
- recv_frame/poll_udp/listener_poll_udp：成功返回完整 bytes 数，无数据 0；BufferTooSmall 不消费队首。
- listener_poll_event：有事件 1，无事件 0。kind=1 DTLS Established；kind=2 Closed，reason 为首个 gfnet 错误的正值，普通关闭为 0。Closed 覆盖尚未投递的 Established。
- `-1 InvalidArgument`、`-2 BufferTooSmall`、`-3 Backpressure`、`-4 ProtocolError`、`-5 AllocationFailed`、`-6 InvalidState`、`-7 InternalError`。

Backpressure 表示未接受该输入/发送；feed 队列满丢新 UDP，业务 send 未复制前可等待。KCP send 接受长度不足、分配失败、output 入队失败锁定 Fatal，不重试整个应用帧。可靠发送成功返回 0 前必须核对 `ikcp_send` 接受长度等于帧长。

输出必须使用 1200-byte datagram 缓冲。应用可靠帧 32～reliable_limit，不可靠帧 32～unreliable_limit。peek_header 在帧小于 32 时锁定 ProtocolError；C# 校验固定头和通道/路由/声明长度后才分配正文，recv 长度必须与固定头一致。

## 状态、时钟与所有权

状态 Handshaking1 → Established2 → PeerClosed3/Fatal4。Established 仅代表 DTLS；GFNP Hello/hash/ticket 绑定完成后才对应用发布 Connected。Fatal 保留首因和 mbedTLS 子因，禁止继续 feed/tick/send/peek/recv/poll UDP；stats/state 和销毁可用。PeerClosed 允许读已排队完整帧，禁止发送。非法参数/错 owner 不自动改状态。

全部 handle 操作必须由 create 线程调用。有效 exclusive handle 只 destroy 一次；已释放指针不可再次调用任何 API。listener 是所有子 handle 的唯一销毁者；event.connection 仅在 release/listener destroy 前有效，子 handle 只允许应用 send/peek/header/recv/stats/state，不允许独立 feed/tick/poll UDP/destroy。

首次 tick 初始化 32-bit 单调毫秒，后续差值必须非负并小于 2^31，允许低位回绕。托管在截断前以 64-bit 检查长时间挂起；固定 10ms update 只由 native 排程，提前 tick 不额外 flush，晚到不补跑。每次实际 flush 之间最多处理 64 个 KCP datagram；单次 tick 的原始输入最多 64。输出队列 256 包/512KiB、最长 250ms；原生所有分配含头计连接 quota，参数上限见 NetworkDesign。

## listener 约定

listener register/revoke/feed/tick/event/poll/bind/close/release 均由 listener owner 串行执行。feed 以完整 peer 和 datagram 为单位；cookie 检验前只有临时握手，输出 cookie 包受共享有界队列约束。验证 cookie 后才占 handshake 槽。PSK 校验查询 Issued grant，不消费；confirm_binding 校验当前 id/generation、未过期 Issued、conv 及 ticket SHA-256，原子 Bound 并终止竞争握手。

Issued TTL 最多 30000ms，重复注册相同 grant 不延长期限。Bound 由应用撤销/连接关闭管理；绑定/撤销清零原始 PSK。DTLS 握手及 Hello 总期限不超过 10s，Established 后 Hello 最多 5s且不延长原始期限。

poll UDP 携带 id/generation/birth；cookie id/generation=0。`listener_get_connection_state` 校验在途包是否仍属活跃实例，也覆盖尚未 Established 的握手；不能因为应用尚未收到 Established event 就丢掉其握手输出。WouldBlock 时保留原始 birth，失效实例丢弃输出，超过队龄关闭对应子连接。关闭/release 一个 child 不影响共享 UDP socket。

终态事件固定存在各子连接槽内，不因普通队列满丢失；release 只接受 PeerClosed/Fatal。release 与 listener_destroy 同步释放 native 所有分配；托管应用任务/缓冲仍须由应用 owner 等待并释放。

## 指标语义

owned/peak 包含 native allocation header 与 quota、KCP、DTLS 和各阶段队列实际容量；pending/inflight_reliable_bytes 为 snd_queue/snd_buf 的有效 payload，不是额外分配额度。udp_sent 字段在旧 C 字段命名中代表 **poll 到托管**，实际 socket 成功发送由 C# 单独累计。timeout_retransmits 是 KCP xmit 的超时重传次数，不含 fast retransmit；不得展示为全部重传。

capabilities=1 只表示基础 KCP/队列/内存统计。锁定 mbedTLS 未开放细分 bad-MAC/replay/cookie 计数，当前明确不可用，不能以 0 冒充已测得。原生额度预留与实际使用分开展示，不重复扣额度。
