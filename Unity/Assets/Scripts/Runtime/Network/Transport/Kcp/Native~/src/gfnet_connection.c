#include "gfnet_internal.h"
_Static_assert(sizeof(void*) == 8, "ABI v1 supports 64-bit targets only");
_Static_assert(sizeof(gfnet_config_v1) == 64, "config layout");
_Static_assert(offsetof(gfnet_config_v1, identity) == 32, "identity offset");
_Static_assert(offsetof(gfnet_config_v1, psk) == 48, "psk offset");
_Static_assert(sizeof(gfnet_stats_v1) == 168, "stats layout");
_Static_assert(sizeof(gfnet_peer_v1) == 24, "peer layout");
_Static_assert(sizeof(gfnet_grant_v1) == 104, "grant layout");
_Static_assert(sizeof(gfnet_event_v1) == 72, "event layout");
_Static_assert(sizeof(gfnet_udp_metadata_v1) == 64, "UDP layout");
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
    mbedtls_ctr_drbg_free(&c->drbg);
    mbedtls_entropy_free(&c->entropy);
    gfnet_queue_clear(&c->input);
    gfnet_queue_clear(&c->output);
    gfnet_queue_clear(&c->plaintext);
    gfnet_queue_clear(&c->unreliable);
    gfnet_free(c);
    gfnet_current_quota = previous;
    gfnet_quota_destroy(quota);
}
int gfnet_connection_create(const gfnet_config_v1* config, gfnet_quota* parent,
    struct gfnet_listener* listener, const gfnet_peer_v1* peer, gfnet_connection** result)
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
    gfnet_initialize();
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
    mbedtls_ssl_cookie_init(&c->cookie); mbedtls_entropy_init(&c->entropy); mbedtls_ctr_drbg_init(&c->drbg);
    static const uint8_t personal[] = "gameframe-kcp-dtls-v1";
    int error = mbedtls_ctr_drbg_seed(&c->drbg, mbedtls_entropy_func, &c->entropy, personal, sizeof(personal)-1);
    if (!error) { error = mbedtls_ssl_config_defaults(&c->config, c->role == GFNET_CLIENT ? MBEDTLS_SSL_IS_CLIENT : MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_DATAGRAM, MBEDTLS_SSL_PRESET_DEFAULT); }
    if (!error)
    {
        mbedtls_ssl_conf_rng(&c->config, mbedtls_ctr_drbg_random, &c->drbg);
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
            error = mbedtls_ssl_cookie_setup(&c->cookie, mbedtls_ctr_drbg_random, &c->drbg);
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
int32_t gfnet_create(const gfnet_config_v1* config, void** handle)
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
        if (length < 0) { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, length); break; }
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
        { error = gfnet_fault(c, GFNET_PROTOCOL_ERROR, status); }
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
            if (status != (int)packet->length) { error = gfnet_fault(c, GFNET_INTERNAL_ERROR, status); break; }
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
int32_t gfnet_peek_frame(void* handle, int32_t* channel, int32_t* length)
{
    gfnet_connection* c = handle; int error = gfnet_check_connection(c);
    if (error) { return error; }
    if (!channel || !length) { return GFNET_INVALID_ARGUMENT; }
    if (c->state == GFNET_FATAL) { return c->fatal_code; }
    *channel=0; *length=0;
    int size = reliable_size(c);
    if (size<0) { return size; }
    if (size) { *channel=1; *length=size; return 1; }
    if (c->unreliable.head) { *channel=2; *length=(int32_t)c->unreliable.head->length; return 1; }
    return 0;
}
int32_t gfnet_peek_frame_header(void* handle, uint8_t* buffer, int32_t capacity)
{
    int32_t channel, length;
    int result = gfnet_peek_frame(handle, &channel, &length);
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
    int32_t channel, length;
    int error = gfnet_peek_frame(handle,&channel,&length);
    if (error <= 0) { return error; }
    if (!buffer || capacity<0) { return GFNET_INVALID_ARGUMENT; }
    if (capacity<length) { return GFNET_BUFFER_TOO_SMALL; }
    gfnet_connection* c=handle;
    if (channel==1)
    {
        int result=ikcp_recv(c->kcp,(char*)buffer,capacity);
        return result == length ? result : gfnet_fault(c,GFNET_INTERNAL_ERROR,result);
    }
    memcpy(buffer,c->unreliable.head->data,(size_t)length);
    gfnet_queue_pop(&c->unreliable); return length;
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
int32_t gfnet_get_stats(void* handle, gfnet_stats_v1* stats)
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
