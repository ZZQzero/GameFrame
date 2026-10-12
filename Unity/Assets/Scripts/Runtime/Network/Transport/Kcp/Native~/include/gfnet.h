#ifndef GFNET_H
#define GFNET_H
#include <stddef.h>
#include <stdint.h>
#if defined(_WIN32)
#define GFNET_API __declspec(dllexport)
#define GFNET_CALL __cdecl
#else
#define GFNET_API __attribute__((visibility("default")))
#define GFNET_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif
#define GFNET_ABI_VERSION 2u
enum gfnet_error { GFNET_INVALID_ARGUMENT=-1, GFNET_BUFFER_TOO_SMALL=-2,
    GFNET_BACKPRESSURE=-3, GFNET_PROTOCOL_ERROR=-4, GFNET_ALLOCATION_FAILED=-5,
    GFNET_INVALID_STATE=-6, GFNET_INTERNAL_ERROR=-7 };
enum gfnet_state { GFNET_HANDSHAKING=1, GFNET_ESTABLISHED=2, GFNET_PEER_CLOSED=3, GFNET_FATAL=4 };
enum gfnet_role { GFNET_CLIENT=1, GFNET_SERVER=2 };
typedef struct gfnet_config_v2 {
    uint32_t struct_size, abi_version, role, conv;
    uint32_t native_limit, reliable_limit, unreliable_limit, reserved;
    const uint8_t* identity;
    uint32_t identity_length, reserved_identity;
    const uint8_t* psk;
    uint32_t psk_length, reserved_psk;
} gfnet_config_v2;
typedef struct gfnet_stats_v2 {
    uint32_t struct_size, abi_version, state, capabilities;
    int32_t fatal_code, native_error;
    uint64_t owned_bytes, peak_bytes, allocations;
    uint64_t udp_received_bytes, udp_sent_bytes, udp_received_packets, udp_sent_packets;
    uint64_t raw_dropped, unreliable_dropped;
    uint32_t input_count, output_count, output_bytes, output_oldest_ms;
    uint32_t waitsnd, srtt_ms, rto_ms, snd_wnd, rcv_wnd, remote_wnd;
    uint32_t plaintext_count, plaintext_bytes;
    uint64_t pending_reliable_bytes, inflight_reliable_bytes, timeout_retransmits;
} gfnet_stats_v2;
typedef struct gfnet_peer_v2 {
    uint32_t address_family;
    uint8_t address[16];
    uint16_t port, reserved;
} gfnet_peer_v2;
typedef struct gfnet_listener_config_v2 {
    uint32_t struct_size, abi_version, native_limit, connection_limit;
    uint32_t max_connections, max_handshakes, max_grants, reserved;
} gfnet_listener_config_v2;
typedef struct gfnet_grant_v2 {
    uint32_t struct_size, abi_version, conv, remaining_ttl_ms;
    uint64_t grant_id;
    uint8_t identity[16], psk[32], ticket_digest[32];
} gfnet_grant_v2;
typedef struct gfnet_event_v2 {
    uint32_t struct_size, abi_version, kind, reason;
    uint64_t connection_id, generation, grant_id;
    void* connection;
    gfnet_peer_v2 peer;
} gfnet_event_v2;
typedef struct gfnet_udp_metadata_v2 {
    uint32_t struct_size, abi_version, enqueued_ms, reserved;
    uint64_t connection_id, generation;
    gfnet_peer_v2 peer;
    uint32_t reserved_tail[2];
} gfnet_udp_metadata_v2;
GFNET_API uint32_t GFNET_CALL gfnet_abi_version(void);
GFNET_API int32_t GFNET_CALL gfnet_create(const gfnet_config_v2*, void**);
GFNET_API int32_t GFNET_CALL gfnet_feed_udp(void*, const uint8_t*, int32_t);
GFNET_API int32_t GFNET_CALL gfnet_tick(void*, uint32_t);
GFNET_API int32_t GFNET_CALL gfnet_send_reliable(void*, const uint8_t*, int32_t);
GFNET_API int32_t GFNET_CALL gfnet_send_unreliable(void*, const uint8_t*, int32_t);
GFNET_API int32_t GFNET_CALL gfnet_poll_udp(void*, uint8_t*, int32_t, uint32_t*);
GFNET_API int32_t GFNET_CALL gfnet_peek_frame(void*, int32_t*, int32_t*, uint32_t*);
GFNET_API int32_t GFNET_CALL gfnet_peek_frame_header(void*, uint8_t*, int32_t);
GFNET_API int32_t GFNET_CALL gfnet_recv_frame(void*, uint8_t*, int32_t);
GFNET_API int32_t GFNET_CALL gfnet_get_stats(void*, gfnet_stats_v2*);
GFNET_API int32_t GFNET_CALL gfnet_get_state(void*, int32_t*);
GFNET_API int32_t GFNET_CALL gfnet_destroy(void*);
GFNET_API int32_t GFNET_CALL gfnet_listener_create(const gfnet_listener_config_v2*, void**);
GFNET_API int32_t GFNET_CALL gfnet_listener_register_grant(void*, const gfnet_grant_v2*, uint32_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_revoke_grant(void*, uint64_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_feed_udp(void*, const gfnet_peer_v2*, const uint8_t*, int32_t, uint32_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_tick(void*, uint32_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_poll_udp(void*, uint8_t*, int32_t, gfnet_udp_metadata_v2*);
GFNET_API int32_t GFNET_CALL gfnet_listener_poll_event(void*, gfnet_event_v2*);
GFNET_API int32_t GFNET_CALL gfnet_listener_confirm_binding(void*, uint64_t, uint64_t, const uint8_t*, int32_t, uint32_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_reject_binding(void*, uint64_t, uint64_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_get_connection_state(void*, uint64_t, uint64_t, int32_t*);
GFNET_API int32_t GFNET_CALL gfnet_listener_close_connection(void*, uint64_t, uint64_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_release_connection(void*, uint64_t, uint64_t);
GFNET_API int32_t GFNET_CALL gfnet_listener_destroy(void*);
#ifdef __cplusplus
}
#endif
#endif
