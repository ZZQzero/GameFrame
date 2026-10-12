#include "gfnet_internal.h"
_Static_assert(sizeof(void*) == 8, "ABI v2 supports 64-bit targets only");
_Static_assert(sizeof(gfnet_config_v2) == 64, "config layout");
_Static_assert(offsetof(gfnet_config_v2, struct_size) == 0, "config.struct_size offset");
_Static_assert(offsetof(gfnet_config_v2, abi_version) == 4, "config.abi_version offset");
_Static_assert(offsetof(gfnet_config_v2, role) == 8, "config.role offset");
_Static_assert(offsetof(gfnet_config_v2, conv) == 12, "config.conv offset");
_Static_assert(offsetof(gfnet_config_v2, native_limit) == 16, "config.native_limit offset");
_Static_assert(offsetof(gfnet_config_v2, reliable_limit) == 20, "config.reliable_limit offset");
_Static_assert(offsetof(gfnet_config_v2, unreliable_limit) == 24, "config.unreliable_limit offset");
_Static_assert(offsetof(gfnet_config_v2, reserved) == 28, "config.reserved offset");
_Static_assert(offsetof(gfnet_config_v2, identity) == 32, "config.identity offset");
_Static_assert(offsetof(gfnet_config_v2, identity_length) == 40, "config.identity_length offset");
_Static_assert(offsetof(gfnet_config_v2, reserved_identity) == 44, "config.reserved_identity offset");
_Static_assert(offsetof(gfnet_config_v2, psk) == 48, "config.psk offset");
_Static_assert(offsetof(gfnet_config_v2, psk_length) == 56, "config.psk_length offset");
_Static_assert(offsetof(gfnet_config_v2, reserved_psk) == 60, "config.reserved_psk offset");
_Static_assert(sizeof(gfnet_stats_v2) == 168, "stats layout");
_Static_assert(offsetof(gfnet_stats_v2, struct_size) == 0, "stats.struct_size offset");
_Static_assert(offsetof(gfnet_stats_v2, abi_version) == 4, "stats.abi_version offset");
_Static_assert(offsetof(gfnet_stats_v2, state) == 8, "stats.state offset");
_Static_assert(offsetof(gfnet_stats_v2, capabilities) == 12, "stats.capabilities offset");
_Static_assert(offsetof(gfnet_stats_v2, fatal_code) == 16, "stats.fatal_code offset");
_Static_assert(offsetof(gfnet_stats_v2, native_error) == 20, "stats.native_error offset");
_Static_assert(offsetof(gfnet_stats_v2, owned_bytes) == 24, "stats.owned_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, peak_bytes) == 32, "stats.peak_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, allocations) == 40, "stats.allocations offset");
_Static_assert(offsetof(gfnet_stats_v2, udp_received_bytes) == 48, "stats.udp_received_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, udp_sent_bytes) == 56, "stats.udp_sent_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, udp_received_packets) == 64, "stats.udp_received_packets offset");
_Static_assert(offsetof(gfnet_stats_v2, udp_sent_packets) == 72, "stats.udp_sent_packets offset");
_Static_assert(offsetof(gfnet_stats_v2, raw_dropped) == 80, "stats.raw_dropped offset");
_Static_assert(offsetof(gfnet_stats_v2, unreliable_dropped) == 88, "stats.unreliable_dropped offset");
_Static_assert(offsetof(gfnet_stats_v2, input_count) == 96, "stats.input_count offset");
_Static_assert(offsetof(gfnet_stats_v2, output_count) == 100, "stats.output_count offset");
_Static_assert(offsetof(gfnet_stats_v2, output_bytes) == 104, "stats.output_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, output_oldest_ms) == 108, "stats.output_oldest_ms offset");
_Static_assert(offsetof(gfnet_stats_v2, waitsnd) == 112, "stats.waitsnd offset");
_Static_assert(offsetof(gfnet_stats_v2, srtt_ms) == 116, "stats.srtt_ms offset");
_Static_assert(offsetof(gfnet_stats_v2, rto_ms) == 120, "stats.rto_ms offset");
_Static_assert(offsetof(gfnet_stats_v2, snd_wnd) == 124, "stats.snd_wnd offset");
_Static_assert(offsetof(gfnet_stats_v2, rcv_wnd) == 128, "stats.rcv_wnd offset");
_Static_assert(offsetof(gfnet_stats_v2, remote_wnd) == 132, "stats.remote_wnd offset");
_Static_assert(offsetof(gfnet_stats_v2, plaintext_count) == 136, "stats.plaintext_count offset");
_Static_assert(offsetof(gfnet_stats_v2, plaintext_bytes) == 140, "stats.plaintext_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, pending_reliable_bytes) == 144, "stats.pending_reliable_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, inflight_reliable_bytes) == 152, "stats.inflight_reliable_bytes offset");
_Static_assert(offsetof(gfnet_stats_v2, timeout_retransmits) == 160, "stats.timeout_retransmits offset");
_Static_assert(sizeof(gfnet_peer_v2) == 24, "peer layout");
_Static_assert(offsetof(gfnet_peer_v2, address_family) == 0, "peer.address_family offset");
_Static_assert(offsetof(gfnet_peer_v2, address) == 4, "peer.address offset");
_Static_assert(offsetof(gfnet_peer_v2, port) == 20, "peer.port offset");
_Static_assert(offsetof(gfnet_peer_v2, reserved) == 22, "peer.reserved offset");
_Static_assert(sizeof(gfnet_listener_config_v2) == 32, "listener_config layout");
_Static_assert(offsetof(gfnet_listener_config_v2, struct_size) == 0, "listener_config.struct_size offset");
_Static_assert(offsetof(gfnet_listener_config_v2, abi_version) == 4, "listener_config.abi_version offset");
_Static_assert(offsetof(gfnet_listener_config_v2, native_limit) == 8, "listener_config.native_limit offset");
_Static_assert(offsetof(gfnet_listener_config_v2, connection_limit) == 12, "listener_config.connection_limit offset");
_Static_assert(offsetof(gfnet_listener_config_v2, max_connections) == 16, "listener_config.max_connections offset");
_Static_assert(offsetof(gfnet_listener_config_v2, max_handshakes) == 20, "listener_config.max_handshakes offset");
_Static_assert(offsetof(gfnet_listener_config_v2, max_grants) == 24, "listener_config.max_grants offset");
_Static_assert(offsetof(gfnet_listener_config_v2, reserved) == 28, "listener_config.reserved offset");
_Static_assert(sizeof(gfnet_grant_v2) == 104, "grant layout");
_Static_assert(offsetof(gfnet_grant_v2, struct_size) == 0, "grant.struct_size offset");
_Static_assert(offsetof(gfnet_grant_v2, abi_version) == 4, "grant.abi_version offset");
_Static_assert(offsetof(gfnet_grant_v2, conv) == 8, "grant.conv offset");
_Static_assert(offsetof(gfnet_grant_v2, remaining_ttl_ms) == 12, "grant.remaining_ttl_ms offset");
_Static_assert(offsetof(gfnet_grant_v2, grant_id) == 16, "grant.grant_id offset");
_Static_assert(offsetof(gfnet_grant_v2, identity) == 24, "grant.identity offset");
_Static_assert(offsetof(gfnet_grant_v2, psk) == 40, "grant.psk offset");
_Static_assert(offsetof(gfnet_grant_v2, ticket_digest) == 72, "grant.ticket_digest offset");
_Static_assert(sizeof(gfnet_event_v2) == 72, "event layout");
_Static_assert(offsetof(gfnet_event_v2, struct_size) == 0, "event.struct_size offset");
_Static_assert(offsetof(gfnet_event_v2, abi_version) == 4, "event.abi_version offset");
_Static_assert(offsetof(gfnet_event_v2, kind) == 8, "event.kind offset");
_Static_assert(offsetof(gfnet_event_v2, reason) == 12, "event.reason offset");
_Static_assert(offsetof(gfnet_event_v2, connection_id) == 16, "event.connection_id offset");
_Static_assert(offsetof(gfnet_event_v2, generation) == 24, "event.generation offset");
_Static_assert(offsetof(gfnet_event_v2, grant_id) == 32, "event.grant_id offset");
_Static_assert(offsetof(gfnet_event_v2, connection) == 40, "event.connection offset");
_Static_assert(offsetof(gfnet_event_v2, peer) == 48, "event.peer offset");
_Static_assert(sizeof(gfnet_udp_metadata_v2) == 64, "udp_metadata layout");
_Static_assert(offsetof(gfnet_udp_metadata_v2, struct_size) == 0, "udp_metadata.struct_size offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, abi_version) == 4, "udp_metadata.abi_version offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, enqueued_ms) == 8, "udp_metadata.enqueued_ms offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, reserved) == 12, "udp_metadata.reserved offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, connection_id) == 16, "udp_metadata.connection_id offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, generation) == 24, "udp_metadata.generation offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, peer) == 32, "udp_metadata.peer offset");
_Static_assert(offsetof(gfnet_udp_metadata_v2, reserved_tail) == 56, "udp_metadata.reserved_tail offset");
static const int ciphers[] = { MBEDTLS_TLS_PSK_WITH_AES_128_GCM_SHA256, 0 };
uint32_t gfnet_abi_version(void) { return GFNET_ABI_VERSION; }
int gfnet_check_connection(gfnet_connection* c)
{
    if (!c || c->magic != GFNET_CONNECTION_MAGIC) { return GFNET_INVALID_ARGUMENT; }
    if (c->thread != gfnet_thread_id()) { return GFNET_INVALID_STATE; }
    return 0;
}
int gfnet_fault(gfnet_connection* c, int error, int native_error)
{
    if (c->state != GFNET_FATAL)
    {
        c->state = GFNET_FATAL;
        c->fatal_code = c->quota->allocation_failed ? GFNET_ALLOCATION_FAILED : error;
        c->native_error = native_error;
        c->closed_event = 1;
    }
    return c->fatal_code;
}
static int bio_send(void* context, const unsigned char* data, size_t length)
{
    gfnet_connection* c = context;
    if (c->state == GFNET_FATAL) { return MBEDTLS_ERR_SSL_INTERNAL_ERROR; }
    if (!length || length > GFNET_MAX_UDP)
    {
        gfnet_fault(c, GFNET_PROTOCOL_ERROR, 0);
        return MBEDTLS_ERR_SSL_BAD_INPUT_DATA;
    }
    int error = gfnet_queue_push(&c->output, data, (uint32_t)length, c->now, GFNET_MAX_DATAGRAMS, GFNET_MAX_OUTPUT_BYTES);
    if (error)
    {
        gfnet_fault(c, error == GFNET_ALLOCATION_FAILED ? error : GFNET_INTERNAL_ERROR, error);
        return MBEDTLS_ERR_SSL_ALLOC_FAILED;
    }
    return (int)length;
}
static int bio_recv(void* context, unsigned char* buffer, size_t capacity)
{
    gfnet_connection* c = context;
    if (!c->input.head || c->input_round >= 64 || c->input_since_flush >= 64)
    {
        return MBEDTLS_ERR_SSL_WANT_READ;
    }
    gfnet_packet* packet = c->input.head;
    if (packet->length > capacity) { return MBEDTLS_ERR_SSL_BUFFER_TOO_SMALL; }
    int length = (int)packet->length;
    memcpy(buffer, packet->data, packet->length);
    gfnet_queue_pop(&c->input);
    c->input_round++;
    return length;
}
static void timer_set(void* context, uint32_t intermediate, uint32_t final)
{
    gfnet_connection* c = context;
    c->timer_start = c->now;
    c->timer_intermediate = intermediate;
    c->timer_final = final;
}
static int timer_get(void* context)
{
    gfnet_connection* c = context;
    if (!c->timer_final) { return -1; }
    uint32_t elapsed = c->now - c->timer_start;
    return elapsed >= c->timer_final ? 2 : elapsed >= c->timer_intermediate ? 1 : 0;
}
static int kcp_output(const char* data, int length, ikcpcb* kcp, void* context)
{
    (void)kcp;
    gfnet_connection* c = context;
    if (c->state == GFNET_FATAL) { return -1; }
    if (length <= 0 || length > GFNET_KCP_MTU) { return gfnet_fault(c, GFNET_PROTOCOL_ERROR, 0); }
    uint8_t plaintext[GFNET_KCP_MTU + 1];
    plaintext[0] = 1;
    memcpy(plaintext + 1, data, (size_t)length);
    int error = gfnet_queue_push(&c->plaintext, plaintext, (uint32_t)length + 1, c->now, GFNET_MAX_DATAGRAMS, GFNET_MAX_OUTPUT_BYTES);
    mbedtls_platform_zeroize(plaintext, sizeof(plaintext));
    if (error) { return gfnet_fault(c, error == GFNET_ALLOCATION_FAILED ? error : GFNET_INTERNAL_ERROR, error); }
    return 0;
}
static int create_kcp(gfnet_connection* c)
{
    c->kcp = ikcp_create(c->conv, c);
    if (!c->kcp) { return gfnet_fault(c, GFNET_ALLOCATION_FAILED, 0); }
    ikcp_setoutput(c->kcp, kcp_output);
    if (ikcp_setmtu(c->kcp, GFNET_KCP_MTU) != 0) { return gfnet_fault(c, GFNET_ALLOCATION_FAILED, -2); }
    ikcp_wndsize(c->kcp, 128, 128);
    ikcp_nodelay(c->kcp, 1, 10, 2, 0);
    c->kcp->rx_minrto = 30;
    c->kcp->dead_link = 20;
    c->kcp->stream = 0;
    c->state = GFNET_ESTABLISHED;
    c->next_tick = c->now;
    c->established_event = 1;
    return 0;
}
void gfnet_connection_free(gfnet_connection* c)
{
    gfnet_quota* quota = c->quota;
    gfnet_quota* previous = gfnet_current_quota;
    gfnet_current_quota = quota;
    if (c->kcp) { ikcp_release(c->kcp); }
    mbedtls_ssl_free(&c->ssl);
    mbedtls_ssl_config_free(&c->config);
    mbedtls_ssl_cookie_free(&c->cookie);
    gfnet_queue_clear(&c->input);
    gfnet_queue_clear(&c->output);
    gfnet_queue_clear(&c->plaintext);
    gfnet_queue_clear(&c->unreliable);
    gfnet_free(c);
    gfnet_current_quota = previous;
    gfnet_quota_destroy(quota);
}
int gfnet_connection_create(const gfnet_config_v2* config, gfnet_quota* parent,
    struct gfnet_listener* listener, const gfnet_peer_v2* peer, gfnet_connection** result)
{
    *result = NULL;
    if (!config || config->struct_size != sizeof(*config) || config->abi_version != GFNET_ABI_VERSION ||
        (config->role != GFNET_CLIENT && config->role != GFNET_SERVER) || !config->conv ||
        config->native_limit < 65536 || config->native_limit > 4u * 1024 * 1024 ||
        config->reliable_limit < 2048 || config->reliable_limit > 32768 ||
        (config->unreliable_limit && (config->unreliable_limit < 256 || config->unreliable_limit > 1150)) ||
        !config->identity || config->identity_length != 16 || !config->psk || config->psk_length != 32 ||
        config->reserved || config->reserved_identity || config->reserved_psk)
    { return GFNET_INVALID_ARGUMENT; }
    int initialization_error = gfnet_initialize();
    if (initialization_error) { return initialization_error; }
    gfnet_quota* quota = gfnet_quota_create(config->native_limit, parent);
    if (!quota) { return GFNET_ALLOCATION_FAILED; }
    gfnet_quota* previous = gfnet_current_quota;
    gfnet_current_quota = quota;
    gfnet_connection* c = gfnet_calloc(1, sizeof(*c));
    if (!c)
    {
        gfnet_current_quota = previous; gfnet_quota_destroy(quota); return GFNET_ALLOCATION_FAILED;
    }
    c->quota = quota; c->magic = GFNET_CONNECTION_MAGIC; c->thread = gfnet_thread_id();
    c->role = config->role; c->conv = config->conv; c->state = GFNET_HANDSHAKING;
    c->reliable_limit = config->reliable_limit; c->unreliable_limit = config->unreliable_limit;
    c->listener = listener;
    if (peer) { c->peer = *peer; }
    memcpy(c->identity, config->identity, 16); memcpy(c->psk, config->psk, 32);
    mbedtls_ssl_init(&c->ssl); mbedtls_ssl_config_init(&c->config);
    mbedtls_ssl_cookie_init(&c->cookie);
    int error = mbedtls_ssl_config_defaults(&c->config, c->role == GFNET_CLIENT ? MBEDTLS_SSL_IS_CLIENT : MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_DATAGRAM, MBEDTLS_SSL_PRESET_DEFAULT);
    if (!error)
    {
        mbedtls_ssl_conf_ciphersuites(&c->config, ciphers);
        mbedtls_ssl_conf_min_tls_version(&c->config, MBEDTLS_SSL_VERSION_TLS1_2);
        mbedtls_ssl_conf_max_tls_version(&c->config, MBEDTLS_SSL_VERSION_TLS1_2);
        mbedtls_ssl_conf_dtls_anti_replay(&c->config, MBEDTLS_SSL_ANTI_REPLAY_ENABLED);
        mbedtls_ssl_conf_handshake_timeout(&c->config, 1000, 8000);
        if (listener) { mbedtls_ssl_conf_psk_cb(&c->config, gfnet_listener_psk, c); }
        else { error = mbedtls_ssl_conf_psk(&c->config, c->psk, 32, c->identity, 16); }
    }
    if (!error && c->role == GFNET_SERVER)
    {
        if (listener) { mbedtls_ssl_conf_dtls_cookies(&c->config, gfnet_listener_cookie_write, gfnet_listener_cookie_check, c); }
        else
        {
            error = mbedtls_ssl_cookie_setup(&c->cookie);
            if (!error) { mbedtls_ssl_conf_dtls_cookies(&c->config, mbedtls_ssl_cookie_write, mbedtls_ssl_cookie_check, &c->cookie); }
        }
    }
    if (!error) { error = mbedtls_ssl_setup(&c->ssl, &c->config); }
    if (!error)
    {
        mbedtls_ssl_set_bio(&c->ssl, c, bio_send, bio_recv, NULL);
        mbedtls_ssl_set_timer_cb(&c->ssl, c, timer_set, timer_get);
        mbedtls_ssl_set_mtu(&c->ssl, GFNET_MAX_UDP);
        if (c->role == GFNET_SERVER)
        {
            uint8_t transport_id[19];
            transport_id[0] = peer ? (uint8_t)peer->address_family : 0;
            memcpy(transport_id + 1, peer ? peer->address : c->identity, 16);
            transport_id[17] = peer ? (uint8_t)(peer->port >> 8) : 0;
            transport_id[18] = peer ? (uint8_t)peer->port : 0;
            error = mbedtls_ssl_set_client_transport_id(&c->ssl, transport_id, sizeof(transport_id));
        }
    }
    gfnet_current_quota = previous;
    if (error)
    {
        int status = quota->allocation_failed ? GFNET_ALLOCATION_FAILED : GFNET_INTERNAL_ERROR;
        gfnet_connection_free(c); return status;
    }
    *result = c; return 0;
}
int32_t gfnet_create(const gfnet_config_v2* config, void** handle)
{
    if (!handle) { return GFNET_INVALID_ARGUMENT; }
    *handle = NULL;
    gfnet_connection* c = NULL;
    int error = gfnet_connection_create(config, NULL, NULL, NULL, &c);
    *handle = c; return error;
}
int32_t gfnet_feed_udp(void* handle, const uint8_t* data, int32_t length)
{
    gfnet_connection* c = handle; int error = gfnet_check_connection(c);
    if (error) { return error; }
    if (c->listener || !c->clock_set || c->state >= GFNET_PEER_CLOSED) { return GFNET_INVALID_STATE; }
    if (!data || length <= 0 || length > GFNET_MAX_UDP) { return GFNET_INVALID_ARGUMENT; }
    gfnet_quota* previous = gfnet_current_quota; gfnet_current_quota = c->quota;
    error = gfnet_queue_push(&c->input, data, (uint32_t)length, c->now, GFNET_MAX_DATAGRAMS, GFNET_MAX_INPUT_BYTES);
    gfnet_current_quota = previous;
    c->udp_rx_packets++; c->udp_rx_bytes += (uint32_t)length;
    if (error == GFNET_BACKPRESSURE) { c->raw_dropped++; return error; }
    return error ? gfnet_fault(c, error, 0) : 0;
}
static int validate_kcp(gfnet_connection* c, const uint8_t* data, int length)
{
    int offset = 0;
    uint32_t fragments = (c->reliable_limit + 1125) / 1126;
    if (length <= 0 || length > GFNET_KCP_MTU) { return 0; }
    while (offset < length)
    {
        if (length-offset < 24 || gfnet_read32(data+offset) != c->conv) { return 0; }
        uint32_t bytes = gfnet_read32(data+offset+20);
        uint8_t command = data[offset+4], fragment = data[offset+5];
        if (bytes > (uint32_t)(length-offset-24)) { return 0; }
        if (command == 81) { if (!bytes || bytes > 1126 || fragment >= fragments) { return 0; } }
        else if ((command != 82 && command != 83 && command != 84) || bytes || fragment) { return 0; }
        offset += 24 + (int)bytes;
    }
    return offset == length;
}
/* Preserve the SSL/PSA cause and distinguish peer input from local failures. */
int gfnet_dtls_fault(gfnet_connection* c, int status)
{
    int category = GFNET_INTERNAL_ERROR;
    /* 4.x returns one SSL or PSA code, never a high/low-level sum. */
    if (c->quota->allocation_failed || status == MBEDTLS_ERR_SSL_ALLOC_FAILED ||
        status == PSA_ERROR_INSUFFICIENT_MEMORY)
    {
        category = GFNET_ALLOCATION_FAILED;
    }
    else
    {
        switch (status)
        {
            case MBEDTLS_ERR_SSL_INVALID_MAC:
            case MBEDTLS_ERR_SSL_INVALID_RECORD:
            case MBEDTLS_ERR_SSL_CONN_EOF:
            case MBEDTLS_ERR_SSL_DECODE_ERROR:
            case MBEDTLS_ERR_SSL_UNSUPPORTED_EXTENSION:
            case MBEDTLS_ERR_SSL_NO_APPLICATION_PROTOCOL:
            case MBEDTLS_ERR_SSL_UNEXPECTED_MESSAGE:
            case MBEDTLS_ERR_SSL_FATAL_ALERT_MESSAGE:
            case MBEDTLS_ERR_SSL_BAD_PROTOCOL_VERSION:
            case MBEDTLS_ERR_SSL_HANDSHAKE_FAILURE:
            case MBEDTLS_ERR_SSL_UNKNOWN_IDENTITY:
            case MBEDTLS_ERR_SSL_TIMEOUT:
            case MBEDTLS_ERR_SSL_UNEXPECTED_RECORD:
            case MBEDTLS_ERR_SSL_ILLEGAL_PARAMETER:
            case PSA_ERROR_INVALID_SIGNATURE:
                category = GFNET_PROTOCOL_ERROR;
                break;
            default:
                break;
        }
    }
    return gfnet_fault(c, category, status);
}
static int read_plaintext(gfnet_connection* c)
{
    uint8_t buffer[GFNET_MAX_UDP];
    int error = 0;
    for (int count=0; count<64 && c->input_since_flush<64; count++)
    {
        int length = mbedtls_ssl_read(&c->ssl, buffer, sizeof(buffer));
        if (length == MBEDTLS_ERR_SSL_WANT_READ || length == MBEDTLS_ERR_SSL_WANT_WRITE) { break; }
        if (length == 0 || length == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY)
        { c->state = GFNET_PEER_CLOSED; c->closed_event = 1; break; }
        if (length < 0) { error = gfnet_dtls_fault(c, length); break; }
        if (length <= 1) { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, 0); break; }
        if (buffer[0] == 1)
        {
            if (!validate_kcp(c, buffer+1, length-1)) { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, 0); break; }
            c->input_since_flush++;
            int status = ikcp_input(c->kcp, (const char*)buffer+1, length-1);
            if (status) { error = gfnet_fault(c, status == -4 ? GFNET_ALLOCATION_FAILED : GFNET_PROTOCOL_ERROR, status); break; }
        }
        else if (buffer[0] == 2)
        {
            if (!c->unreliable_limit || length-1 > c->unreliable_limit) { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, 0); break; }
            int status;
            do
            {
                status = gfnet_queue_push(&c->unreliable, buffer+1, (uint32_t)length-1, c->now, 64, 64*1024);
                if (status == GFNET_BACKPRESSURE) { gfnet_queue_pop(&c->unreliable); c->unreliable_dropped++; }
            } while (status == GFNET_BACKPRESSURE);
            if (status) { error = gfnet_fault(c, status, 0); break; }
        }
        else { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, 0); break; }
    }
    mbedtls_platform_zeroize(buffer, sizeof(buffer)); return error;
}
int gfnet_connection_tick(gfnet_connection* c, uint32_t now)
{
    if (c->state == GFNET_FATAL) { return c->fatal_code; }
    if (c->state == GFNET_PEER_CLOSED) { return 0; }
    if (c->clock_set && (int32_t)(now-c->now)<0) { return GFNET_INVALID_ARGUMENT; }
    c->now = now; c->clock_set = 1; c->input_round = 0;
    gfnet_quota* previous = gfnet_current_quota; gfnet_current_quota = c->quota;
    int error = 0;
    if (c->output.head && now-c->output.head->born > 250) { error = gfnet_fault(c, GFNET_INTERNAL_ERROR, GFNET_BACKPRESSURE); }
    if (!error && c->state == GFNET_HANDSHAKING)
    {
        int status = mbedtls_ssl_handshake(&c->ssl);
        if (!status) { error = create_kcp(c); }
        else if (status == MBEDTLS_ERR_SSL_HELLO_VERIFY_REQUIRED)
        {
            c->hello_verify = 1;
            if (!c->listener)
            {
                status = mbedtls_ssl_session_reset(&c->ssl);
                uint8_t id[19] = {0}; memcpy(id+1, c->identity, 16);
                if (!status) { status = mbedtls_ssl_set_client_transport_id(&c->ssl, id, sizeof(id)); }
                if (status) { error = gfnet_fault(c, GFNET_INTERNAL_ERROR, status); }
            }
        }
        else if (status != MBEDTLS_ERR_SSL_WANT_READ && status != MBEDTLS_ERR_SSL_WANT_WRITE)
        { error = gfnet_dtls_fault(c, status); }
    }
    if (!error && c->state == GFNET_ESTABLISHED)
    {
        error = read_plaintext(c);
        if (!error && c->state == GFNET_ESTABLISHED && gfnet_due(now, c->next_tick))
        {
            uint32_t previous_flush = c->kcp->ts_flush;
            int first = !c->kcp->updated;
            ikcp_update(c->kcp, now);
            c->next_tick = now + 10;
            if (first || previous_flush != c->kcp->ts_flush) { c->input_since_flush = 0; }
            if (c->kcp->state == UINT32_MAX) { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, -1); }
        }
        for (int count=0; !error && c->state == GFNET_ESTABLISHED && c->plaintext.head && count<256; count++)
        {
            gfnet_packet* packet = c->plaintext.head;
            if (now-packet->born > 250) { error = gfnet_fault(c, GFNET_INTERNAL_ERROR, GFNET_BACKPRESSURE); break; }
            int status = mbedtls_ssl_write(&c->ssl, packet->data, packet->length);
            if (status == MBEDTLS_ERR_SSL_WANT_READ || status == MBEDTLS_ERR_SSL_WANT_WRITE) { break; }
            if (status != (int)packet->length) { error = status < 0 ? gfnet_dtls_fault(c, status) : gfnet_fault(c, GFNET_INTERNAL_ERROR, status); break; }
            gfnet_queue_pop(&c->plaintext);
        }
    }
    if (c->quota->allocation_failed) { error = gfnet_fault(c, GFNET_ALLOCATION_FAILED, 0); }
    if (c->state == GFNET_FATAL) { error = c->fatal_code; }
    gfnet_current_quota = previous; return error;
}
int32_t gfnet_tick(void* handle, uint32_t now)
{
    gfnet_connection* c = handle; int error = gfnet_check_connection(c);
    if (error) { return error; }
    if (c->listener) { return GFNET_INVALID_STATE; }
    return gfnet_connection_tick(c, now);
}
int32_t gfnet_send_reliable(void* handle, const uint8_t* data, int32_t length)
{
    gfnet_connection* c = handle; int error = gfnet_check_connection(c);
    if (error) { return error; }
    if (c->state != GFNET_ESTABLISHED) { return c->state == GFNET_FATAL ? c->fatal_code : GFNET_INVALID_STATE; }
    if (!data || length < 32 || length > c->reliable_limit) { return GFNET_INVALID_ARGUMENT; }
    if (ikcp_waitsnd(c->kcp) + (length+1125)/1126 > 512) { return GFNET_BACKPRESSURE; }
    gfnet_quota* previous = gfnet_current_quota; gfnet_current_quota = c->quota;
    int result = ikcp_send(c->kcp, (const char*)data, length);
    gfnet_current_quota = previous;
    return result == length ? 0 : gfnet_fault(c, result == -2 ? GFNET_ALLOCATION_FAILED : GFNET_INTERNAL_ERROR, result);
}
int32_t gfnet_send_unreliable(void* handle, const uint8_t* data, int32_t length)
{
    gfnet_connection* c = handle; int error = gfnet_check_connection(c);
    if (error) { return error; }
    if (c->state != GFNET_ESTABLISHED) { return c->state == GFNET_FATAL ? c->fatal_code : GFNET_INVALID_STATE; }
    if (!data || length<32 || !c->unreliable_limit || length>c->unreliable_limit) { return GFNET_INVALID_ARGUMENT; }
    uint8_t buffer[1151]; buffer[0]=2; memcpy(buffer+1,data,(size_t)length);
    gfnet_quota* previous = gfnet_current_quota; gfnet_current_quota = c->quota;
    error = gfnet_queue_push(&c->plaintext, buffer, (uint32_t)length+1, c->now, GFNET_MAX_DATAGRAMS, GFNET_MAX_OUTPUT_BYTES);
    gfnet_current_quota = previous; mbedtls_platform_zeroize(buffer,sizeof(buffer));
    return error == GFNET_ALLOCATION_FAILED ? gfnet_fault(c,error,0) : error;
}
static int reliable_size(gfnet_connection* c)
{
    if (!c->kcp || iqueue_is_empty(&c->kcp->rcv_queue)) { return 0; }
    struct IQUEUEHEAD* p = c->kcp->rcv_queue.next;
    struct IKCPSEG* first = iqueue_entry(p, struct IKCPSEG, node);
    uint32_t expected = first->frg;
    uint64_t bytes = 0;
    for (; p != &c->kcp->rcv_queue; p = p->next)
    {
        struct IKCPSEG* segment = iqueue_entry(p, struct IKCPSEG, node);
        if (segment->frg != expected) { return gfnet_fault(c,GFNET_PROTOCOL_ERROR,0); }
        bytes += segment->len;
        if (bytes > c->reliable_limit) { return gfnet_fault(c,GFNET_PROTOCOL_ERROR,0); }
        if (!expected) { return (int)bytes; }
        expected--;
    }
    return 0;
}
int32_t gfnet_peek_frame(void* handle, int32_t* channel, int32_t* length, uint32_t* born)
{
    gfnet_connection* c = handle; int error = gfnet_check_connection(c);
    if (error) { return error; }
    if (!channel || !length || !born) { return GFNET_INVALID_ARGUMENT; }
    if (c->state == GFNET_FATAL) { return c->fatal_code; }
    *channel=0; *length=0; *born=0;
    int size = reliable_size(c);
    if (size<0) { return size; }
    /* Only consumption advances the 16 reliable / 4 unreliable turn. All peeks
       and BufferTooSmall retries must refer to the same head. */
    if (c->unreliable.head && (!size || c->receive_turn >= 16))
    {
        *channel=2; *length=(int32_t)c->unreliable.head->length; *born=c->unreliable.head->born; return 1;
    }
    if (size) { *channel=1; *length=size; *born=c->now; return 1; }
    return 0;
}
int32_t gfnet_peek_frame_header(void* handle, uint8_t* buffer, int32_t capacity)
{
    int32_t channel, length; uint32_t born;
    int result = gfnet_peek_frame(handle, &channel, &length, &born);
    if (result <= 0) { return result; }
    if (!buffer || capacity < 0) { return GFNET_INVALID_ARGUMENT; }
    if (length < 32) { return gfnet_fault(handle, GFNET_PROTOCOL_ERROR, 0); }
    if (capacity < 32) { return GFNET_BUFFER_TOO_SMALL; }
    gfnet_connection* c = handle;
    if (channel == 2) { memcpy(buffer, c->unreliable.head->data, 32); return 32; }
    uint32_t copied = 0;
    for (struct IQUEUEHEAD* p = c->kcp->rcv_queue.next; p != &c->kcp->rcv_queue && copied < 32; p = p->next)
    {
        struct IKCPSEG* segment = iqueue_entry(p, struct IKCPSEG, node);
        uint32_t count = segment->len < 32 - copied ? segment->len : 32 - copied;
        memcpy(buffer + copied, segment->data, count); copied += count;
    }
    return copied == 32 ? 32 : gfnet_fault(c, GFNET_INTERNAL_ERROR, 0);
}
int32_t gfnet_recv_frame(void* handle, uint8_t* buffer, int32_t capacity)
{
    int32_t channel, length; uint32_t born;
    int error = gfnet_peek_frame(handle,&channel,&length,&born);
    if (error <= 0) { return error; }
    if (!buffer || capacity<0) { return GFNET_INVALID_ARGUMENT; }
    if (capacity<length) { return GFNET_BUFFER_TOO_SMALL; }
    gfnet_connection* c=handle;
    if (channel==1)
    {
        int result=ikcp_recv(c->kcp,(char*)buffer,capacity);
        if (result == length && c->receive_turn < 16) { c->receive_turn++; }
        return result == length ? result : gfnet_fault(c,GFNET_INTERNAL_ERROR,result);
    }
    memcpy(buffer,c->unreliable.head->data,(size_t)length);
    gfnet_queue_pop(&c->unreliable);
    c->receive_turn = c->receive_turn < 16 ? 17 : c->receive_turn + 1;
    if (c->receive_turn == 20) { c->receive_turn=0; }
    return length;
}
int32_t gfnet_poll_udp(void* handle, uint8_t* buffer, int32_t capacity, uint32_t* enqueued_ms)
{
    gfnet_connection* c=handle; int error=gfnet_check_connection(c);
    if (error) { return error; }
    if (c->listener) { return GFNET_INVALID_STATE; }
    if (c->state==GFNET_FATAL) { return c->fatal_code; }
    if (!buffer || capacity<0 || !enqueued_ms) { return GFNET_INVALID_ARGUMENT; }
    if (!c->output.head) { return 0; }
    int length=(int)c->output.head->length;
    if (capacity<length) { return GFNET_BUFFER_TOO_SMALL; }
    memcpy(buffer,c->output.head->data,(size_t)length); *enqueued_ms=c->output.head->born;
    gfnet_queue_pop(&c->output); c->udp_tx_packets++; c->udp_tx_bytes+=(uint32_t)length;
    return length;
}
int32_t gfnet_get_state(void* handle, int32_t* state)
{
    gfnet_connection* c=handle; int error=gfnet_check_connection(c);
    if (error) { return error; }
    if (!state) { return GFNET_INVALID_ARGUMENT; }
    *state=c->state; return 0;
}
int32_t gfnet_get_stats(void* handle, gfnet_stats_v2* stats)
{
    gfnet_connection* c=handle; int error=gfnet_check_connection(c);
    if (error) { return error; }
    if (!stats || stats->struct_size != sizeof(*stats) || stats->abi_version != GFNET_ABI_VERSION) { return GFNET_INVALID_ARGUMENT; }
    memset(stats,0,sizeof(*stats)); stats->struct_size=sizeof(*stats); stats->abi_version=GFNET_ABI_VERSION;
    stats->state=(uint32_t)c->state; stats->capabilities=1; stats->fatal_code=c->fatal_code; stats->native_error=c->native_error;
    stats->owned_bytes=c->quota->bytes; stats->peak_bytes=c->quota->peak; stats->allocations=c->quota->allocations;
    stats->udp_received_bytes=c->udp_rx_bytes; stats->udp_sent_bytes=c->udp_tx_bytes;
    stats->udp_received_packets=c->udp_rx_packets; stats->udp_sent_packets=c->udp_tx_packets;
    stats->raw_dropped=c->raw_dropped; stats->unreliable_dropped=c->unreliable_dropped;
    stats->plaintext_count=c->plaintext.count; stats->plaintext_bytes=c->plaintext.bytes;
    stats->input_count=c->input.count; stats->output_count=c->output.count; stats->output_bytes=c->output.bytes;
    stats->output_oldest_ms=c->output.head ? c->now-c->output.head->born : 0;
    if (c->kcp)
    {
        stats->waitsnd=(uint32_t)ikcp_waitsnd(c->kcp); stats->srtt_ms=(uint32_t)c->kcp->rx_srtt; stats->rto_ms=(uint32_t)c->kcp->rx_rto;
        stats->timeout_retransmits=c->kcp->xmit;
        for (struct IQUEUEHEAD* p=c->kcp->snd_queue.next; p!=&c->kcp->snd_queue; p=p->next)
        { stats->pending_reliable_bytes+=iqueue_entry(p,struct IKCPSEG,node)->len; }
        for (struct IQUEUEHEAD* p=c->kcp->snd_buf.next; p!=&c->kcp->snd_buf; p=p->next)
        { stats->inflight_reliable_bytes+=iqueue_entry(p,struct IKCPSEG,node)->len; }
        stats->snd_wnd=c->kcp->snd_wnd; stats->rcv_wnd=c->kcp->rcv_wnd; stats->remote_wnd=c->kcp->rmt_wnd;
    }
    return 0;
}
int32_t gfnet_destroy(void* handle)
{
    gfnet_connection* c=handle; int error=gfnet_check_connection(c);
    if (error) { return error; }
    if (c->listener) { return GFNET_INVALID_STATE; }
    gfnet_connection_free(c); return 0;
}
