#ifndef GFNET_INTERNAL_H
#define GFNET_INTERNAL_H
#include "gfnet.h"
#include "ikcp.h"
#include "mbedtls/ssl.h"
#include "mbedtls/ssl_cookie.h"
#include "mbedtls/entropy.h"
#include "mbedtls/ctr_drbg.h"
#include "mbedtls/platform.h"
#include "mbedtls/platform_util.h"
#include <limits.h>
#include <stdlib.h>
#include <string.h>
#if defined(_WIN32)
#include <windows.h>
#define GFNET_TLS __declspec(thread)
#else
#include <pthread.h>
#define GFNET_TLS _Thread_local
#endif
#define GFNET_CONNECTION_MAGIC 0x47464e43u
#define GFNET_LISTENER_MAGIC 0x47464e4cu
#define GFNET_MAX_UDP 1200u
#define GFNET_KCP_MTU 1150u
#define GFNET_MAX_OUTPUT_BYTES (512u * 1024u)
#define GFNET_MAX_INPUT_BYTES (256u * 1024u)
#define GFNET_MAX_DATAGRAMS 256u
typedef struct gfnet_quota {
    struct gfnet_quota* parent;
    uint64_t bytes, peak, allocations, limit;
    int allocation_failed;
    int64_t fail_after;
} gfnet_quota;
typedef struct gfnet_packet {
    struct gfnet_packet* next;
    uint32_t length, born;
    uint8_t data[];
} gfnet_packet;
typedef struct gfnet_queue {
    gfnet_packet* head;
    gfnet_packet* tail;
    uint32_t count, bytes;
} gfnet_queue;
struct gfnet_listener;
typedef struct gfnet_connection {
    uint32_t magic, role, conv, reliable_limit, unreliable_limit;
    uintptr_t thread;
    gfnet_quota* quota;
    struct gfnet_listener* listener;
    uint64_t connection_id, generation, grant_id;
    gfnet_peer_v1 peer;
    int state, fatal_code, native_error, clock_set, cookie_verified, hello_verify;
    int bound, established_event, closed_event;
    uint32_t now, next_tick, input_since_flush, input_round;
    uint32_t timer_start, timer_intermediate, timer_final;
    uint32_t hello_deadline;
    uint8_t identity[16], psk[32];
    mbedtls_ssl_context ssl;
    mbedtls_ssl_config config;
    mbedtls_ssl_cookie_ctx cookie;
    mbedtls_entropy_context entropy;
    mbedtls_ctr_drbg_context drbg;
    ikcpcb* kcp;
    gfnet_queue input, output, plaintext, unreliable;
    uint64_t udp_rx_bytes, udp_tx_bytes, udp_rx_packets, udp_tx_packets;
    uint64_t raw_dropped, unreliable_dropped;
} gfnet_connection;
extern GFNET_TLS gfnet_quota* gfnet_current_quota;
#ifdef GFNET_TESTING
extern GFNET_TLS int64_t gfnet_test_fail_after;
#endif
uintptr_t gfnet_thread_id(void);
void gfnet_initialize(void);
gfnet_quota* gfnet_quota_create(uint64_t, gfnet_quota*);
void gfnet_quota_destroy(gfnet_quota*);
void* gfnet_alloc(size_t);
void* gfnet_calloc(size_t, size_t);
void gfnet_free(void*);
uint64_t gfnet_memory_live(void);
int gfnet_queue_push(gfnet_queue*, const uint8_t*, uint32_t, uint32_t, uint32_t, uint32_t);
void gfnet_queue_pop(gfnet_queue*);
void gfnet_queue_clear(gfnet_queue*);
int gfnet_check_connection(gfnet_connection*);
int gfnet_fault(gfnet_connection*, int, int);
int gfnet_connection_create(const gfnet_config_v1*, gfnet_quota*, struct gfnet_listener*, const gfnet_peer_v1*, gfnet_connection**);
void gfnet_connection_free(gfnet_connection*);
int gfnet_connection_tick(gfnet_connection*, uint32_t);
int gfnet_listener_psk(void*, mbedtls_ssl_context*, const unsigned char*, size_t);
int gfnet_listener_cookie_write(void*, unsigned char**, unsigned char*, const unsigned char*, size_t);
int gfnet_listener_cookie_check(void*, const unsigned char*, size_t, const unsigned char*, size_t);
static inline int gfnet_due(uint32_t now, uint32_t deadline) { return (int32_t)(now - deadline) >= 0; }
static inline uint32_t gfnet_read32(const uint8_t* p) { return (uint32_t)p[0] | (uint32_t)p[1]<<8 | (uint32_t)p[2]<<16 | (uint32_t)p[3]<<24; }
#endif
