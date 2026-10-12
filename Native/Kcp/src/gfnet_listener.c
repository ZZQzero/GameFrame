#include "gfnet_internal.h"
typedef struct gfnet_grant_slot {
    gfnet_grant_v2 value;
    uint32_t expiry;
    int state;
} gfnet_grant_slot;
typedef struct gfnet_listener {
    uint32_t magic, now, connection_limit, max_connections, max_handshakes, max_grants, slot_count;
    uintptr_t thread;
    int clock_set, prefer_cookie;
    uint32_t output_cursor;
    uint64_t next_id;
    gfnet_quota* quota;
    gfnet_connection** connections;
    gfnet_grant_slot* grants;
    gfnet_queue cookies;
    mbedtls_ssl_cookie_ctx cookie;
} gfnet_listener;
static int check_listener(gfnet_listener* l)
{
    if (!l || l->magic != GFNET_LISTENER_MAGIC) { return GFNET_INVALID_ARGUMENT; }
    return l->thread == gfnet_thread_id() ? 0 : GFNET_INVALID_STATE;
}
static int advance(gfnet_listener* l, uint32_t now)
{
    if (l->clock_set && (int32_t)(now-l->now)<0) { return GFNET_INVALID_ARGUMENT; }
    l->now=now; l->clock_set=1; return 0;
}
static int valid_peer(const gfnet_peer_v2* peer)
{
    if (!peer || peer->reserved || !peer->port || (peer->address_family!=4 && peer->address_family!=6)) { return 0; }
    if (peer->address_family==4)
    {
        for (int i=4; i<16; i++) { if (peer->address[i]) { return 0; } }
    }
    if (peer->address_family==6 && peer->address[0]==0xfe && (peer->address[1]&0xc0)==0x80) { return 0; }
    return 1;
}
static gfnet_connection* find_connection(gfnet_listener* l, uint64_t id, uint64_t generation)
{
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* c=l->connections[i];
        if (c && c->connection_id==id && c->generation==generation) { return c; }
    }
    return NULL;
}
static gfnet_grant_slot* find_grant(gfnet_listener* l, uint64_t id)
{
    for (uint32_t i=0; i<l->max_grants; i++) { if (l->grants[i].state && l->grants[i].value.grant_id==id) { return &l->grants[i]; } }
    return NULL;
}
int gfnet_listener_psk(void* context, mbedtls_ssl_context* ssl, const unsigned char* identity, size_t length)
{
    gfnet_connection* c=context;
    gfnet_listener* l=c->listener;
    if (!c->cookie_verified || length!=16) { return MBEDTLS_ERR_SSL_UNKNOWN_IDENTITY; }
    for (uint32_t i=0; i<l->max_grants; i++)
    {
        gfnet_grant_slot* g=&l->grants[i];
        if (g->state==1 && !gfnet_due(c->now,g->expiry) && memcmp(identity,g->value.identity,16)==0)
        {
            c->grant_id=g->value.grant_id; c->conv=g->value.conv;
            memcpy(c->identity,g->value.identity,16);
            return mbedtls_ssl_set_hs_psk(ssl,g->value.psk,32);
        }
    }
    return MBEDTLS_ERR_SSL_UNKNOWN_IDENTITY;
}
int gfnet_listener_cookie_write(void* context, unsigned char** p, unsigned char* end, const unsigned char* id, size_t length)
{
    gfnet_connection* c=context;
    return mbedtls_ssl_cookie_write(&c->listener->cookie,p,end,id,length);
}
int gfnet_listener_cookie_check(void* context, const unsigned char* cookie, size_t length, const unsigned char* id, size_t id_length)
{
    gfnet_connection* c=context;
    int result=mbedtls_ssl_cookie_check(&c->listener->cookie,cookie,length,id,id_length);
    if (!result) { c->cookie_verified=1; }
    return result;
}
int32_t gfnet_listener_create(const gfnet_listener_config_v2* config, void** handle)
{
    if (!handle) { return GFNET_INVALID_ARGUMENT; }
    *handle=NULL;
    if (!config || config->struct_size!=sizeof(*config) || config->abi_version!=GFNET_ABI_VERSION || config->reserved ||
        config->native_limit<65536 || config->native_limit>256u*1024*1024 ||
        config->connection_limit<65536 || config->connection_limit>4u*1024*1024 ||
        !config->max_connections || config->max_connections>64 || !config->max_handshakes || config->max_handshakes>16 ||
        !config->max_grants || config->max_grants>256) { return GFNET_INVALID_ARGUMENT; }
    int initialization_error = gfnet_initialize();
    if (initialization_error) { return initialization_error; }
    gfnet_quota* quota=gfnet_quota_create(config->native_limit,NULL);
    if (!quota) { return GFNET_ALLOCATION_FAILED; }
    gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=quota;
    gfnet_listener* l=gfnet_calloc(1,sizeof(*l));
    if (!l) { gfnet_current_quota=previous; gfnet_quota_destroy(quota); return GFNET_ALLOCATION_FAILED; }
    l->quota=quota; l->magic=GFNET_LISTENER_MAGIC; l->thread=gfnet_thread_id();
    l->connection_limit=config->connection_limit; l->max_connections=config->max_connections;
    l->max_handshakes=config->max_handshakes; l->max_grants=config->max_grants;
    l->slot_count=l->max_connections+l->max_handshakes;
    l->connections=gfnet_calloc(l->slot_count,sizeof(*l->connections));
    l->grants=gfnet_calloc(l->max_grants,sizeof(*l->grants));
    mbedtls_ssl_cookie_init(&l->cookie);
    int status=quota->allocation_failed ? MBEDTLS_ERR_SSL_ALLOC_FAILED : mbedtls_ssl_cookie_setup(&l->cookie);
    gfnet_current_quota=previous;
    if (status)
    {
        int error=quota->allocation_failed ? GFNET_ALLOCATION_FAILED : GFNET_INTERNAL_ERROR;
        gfnet_listener_destroy(l); return error;
    }
    *handle=l; return 0;
}
int32_t gfnet_listener_register_grant(void* handle, const gfnet_grant_v2* grant, uint32_t now)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if (!grant || grant->struct_size!=sizeof(*grant) || grant->abi_version!=GFNET_ABI_VERSION ||
        !grant->grant_id || !grant->conv || !grant->remaining_ttl_ms || grant->remaining_ttl_ms>30000) { return GFNET_INVALID_ARGUMENT; }
    if ((error=advance(l,now))) { return error; }
    gfnet_grant_slot* slot=find_grant(l,grant->grant_id);
    if (slot)
    {
        if (slot->state!=1 || gfnet_due(now,slot->expiry) || slot->value.conv!=grant->conv ||
            memcmp(slot->value.identity,grant->identity,80)) { return GFNET_INVALID_STATE; }
        return 0;
    }
    for (uint32_t i=0; i<l->max_grants; i++)
    {
        gfnet_grant_slot* g=&l->grants[i];
        if (g->state && (g->value.conv==grant->conv || memcmp(g->value.identity,grant->identity,16)==0)) { return GFNET_INVALID_STATE; }
    }
    for (uint32_t i=0; i<l->max_grants; i++)
    {
        if (!l->grants[i].state)
        {
            slot=&l->grants[i]; slot->value=*grant; slot->expiry=now+grant->remaining_ttl_ms; slot->state=1; return 0;
        }
    }
    return GFNET_BACKPRESSURE;
}
int32_t gfnet_listener_revoke_grant(void* handle, uint64_t id)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    gfnet_grant_slot* grant=find_grant(l,id);
    if (!grant) { return GFNET_INVALID_ARGUMENT; }
    grant->state=3;
    mbedtls_platform_zeroize(grant->value.psk,32);
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* c=l->connections[i];
        if (c && c->grant_id==id) { gfnet_fault(c,GFNET_INVALID_STATE,0); }
    }
    return 0;
}
static int enqueue_cookie(gfnet_listener* l, gfnet_connection* c)
{
    uint8_t envelope[sizeof(gfnet_peer_v2)+GFNET_MAX_UDP];
    gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=l->quota;
    int error=0;
    while (c->output.head)
    {
        memcpy(envelope,&c->peer,sizeof(c->peer));
        memcpy(envelope+sizeof(c->peer),c->output.head->data,c->output.head->length);
        error=gfnet_queue_push(&l->cookies,envelope,(uint32_t)sizeof(c->peer)+c->output.head->length,c->now,256,512*1024);
        gfnet_queue_pop(&c->output);
        if (error) { break; }
    }
    gfnet_current_quota=previous; mbedtls_platform_zeroize(envelope,sizeof(envelope)); return error;
}
int32_t gfnet_listener_feed_udp(void* handle, const gfnet_peer_v2* peer, const uint8_t* data, int32_t length, uint32_t now)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if (!valid_peer(peer) || !data || length<=0 || length>GFNET_MAX_UDP) { return GFNET_INVALID_ARGUMENT; }
    if ((error=advance(l,now))) { return error; }
    gfnet_connection* c=NULL;
    uint32_t free_slot=l->slot_count, handshakes=0;
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* current=l->connections[i];
        if (!current) { if (free_slot==l->slot_count) { free_slot=i; } continue; }
        if (current->state==GFNET_HANDSHAKING) { handshakes++; }
        if (memcmp(&current->peer,peer,sizeof(*peer))==0) { c=current; }
    }
    if (c && c->state>=GFNET_PEER_CLOSED) { return GFNET_INVALID_STATE; }
    int transient=c==NULL;
    if (transient)
    {
        if (free_slot==l->slot_count || l->next_id==UINT64_MAX) { return GFNET_BACKPRESSURE; }
        uint8_t identity[16]={0}, psk[32]={0};
        gfnet_config_v2 config={sizeof(config),GFNET_ABI_VERSION,GFNET_SERVER,1,l->connection_limit,32768,1150,0,identity,16,0,psk,32,0};
        error=gfnet_connection_create(&config,l->quota,l,peer,&c);
        if (error) { return error; }
        c->now=now; c->clock_set=1; c->hello_deadline=now+10000;
    }
    gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=c->quota;
    error=gfnet_queue_push(&c->input,data,(uint32_t)length,now,256,256*1024);
    gfnet_current_quota=previous;
    c->udp_rx_packets++; c->udp_rx_bytes+=(uint32_t)length;
    if (error==GFNET_BACKPRESSURE) { c->raw_dropped++; }
    else if (error) { gfnet_fault(c,error,0); }
    if (transient)
    {
        if (!error) { error=gfnet_connection_tick(c,now); }
        if (!c->cookie_verified || c->hello_verify || error)
        {
            int output_error=enqueue_cookie(l,c);
            gfnet_connection_free(c);
            return error ? error : output_error;
        }
        if (handshakes>=l->max_handshakes)
        {
            gfnet_connection_free(c); return GFNET_BACKPRESSURE;
        }
        c->connection_id=++l->next_id; c->generation=c->connection_id;
        l->connections[free_slot]=c;
    }
    return error;
}
int32_t gfnet_listener_tick(void* handle, uint32_t now)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if ((error=advance(l,now))) { return error; }
    while (l->cookies.head && now-l->cookies.head->born>250) { gfnet_queue_pop(&l->cookies); }
    uint32_t established=0;
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* c=l->connections[i];
        if (c && c->state==GFNET_ESTABLISHED) { established++; }
    }
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* c=l->connections[i];
        if (!c) { continue; }
        if (!c->bound && gfnet_due(now,c->hello_deadline)) { gfnet_fault(c,GFNET_PROTOCOL_ERROR,0); }
        int handshaking=c->state==GFNET_HANDSHAKING;
        gfnet_connection_tick(c,now);
        if (handshaking && c->state==GFNET_ESTABLISHED)
        {
            if (established>=l->max_connections) { gfnet_fault(c,GFNET_BACKPRESSURE,0); }
            else
            {
                established++;
                if (gfnet_due(c->hello_deadline, now+5000)) { c->hello_deadline=now+5000; }
            }
        }
    }
    for (uint32_t i=0; i<l->max_grants; i++)
    {
        gfnet_grant_slot* g=&l->grants[i];
        if (g->state==2 || !g->state || !gfnet_due(now,g->expiry)) { continue; }
        for (uint32_t j=0; j<l->slot_count; j++)
        {
            gfnet_connection* c=l->connections[j];
            if (c && !c->bound && c->grant_id==g->value.grant_id) { gfnet_fault(c,GFNET_PROTOCOL_ERROR,0); }
        }
        mbedtls_platform_zeroize(g,sizeof(*g));
    }
    return l->quota->allocation_failed ? GFNET_ALLOCATION_FAILED : 0;
}
int32_t gfnet_listener_poll_udp(void* handle, uint8_t* dst, int32_t capacity, gfnet_udp_metadata_v2* metadata)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if (!dst || capacity<0 || !metadata || metadata->struct_size!=sizeof(*metadata) || metadata->abi_version!=GFNET_ABI_VERSION) { return GFNET_INVALID_ARGUMENT; }
    gfnet_packet* packet=NULL;
    gfnet_connection* c=NULL;
    if (l->prefer_cookie && l->cookies.head) { packet=l->cookies.head; }
    if (!packet)
    {
        for (uint32_t n=0; n<l->slot_count; n++)
        {
            uint32_t i=(l->output_cursor+n)%l->slot_count;
            gfnet_connection* current=l->connections[i];
            if (current && current->state<GFNET_PEER_CLOSED && current->output.head)
            { c=current; packet=current->output.head; l->output_cursor=(i+1)%l->slot_count; break; }
        }
    }
    if (!packet) { packet=l->cookies.head; }
    if (!packet) { return 0; }
    uint32_t offset=c ? 0 : sizeof(gfnet_peer_v2);
    int length=(int)(packet->length-offset);
    if (capacity<length) { return GFNET_BUFFER_TOO_SMALL; }
    l->prefer_cookie=c!=NULL;
    memset(metadata,0,sizeof(*metadata)); metadata->struct_size=sizeof(*metadata); metadata->abi_version=GFNET_ABI_VERSION;
    metadata->enqueued_ms=packet->born;
    if (c)
    {
        metadata->connection_id=c->connection_id; metadata->generation=c->generation; metadata->peer=c->peer;
        c->udp_tx_packets++; c->udp_tx_bytes+=(uint32_t)length;
    }
    else { memcpy(&metadata->peer,packet->data,sizeof(metadata->peer)); }
    memcpy(dst,packet->data+offset,(size_t)length);
    gfnet_queue_pop(c ? &c->output : &l->cookies); return length;
}
int32_t gfnet_listener_poll_event(void* handle, gfnet_event_v2* event)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if (!event || event->struct_size!=sizeof(*event) || event->abi_version!=GFNET_ABI_VERSION) { return GFNET_INVALID_ARGUMENT; }
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* c=l->connections[i];
        if (!c || (!c->closed_event && !c->established_event)) { continue; }
        memset(event,0,sizeof(*event)); event->struct_size=sizeof(*event); event->abi_version=GFNET_ABI_VERSION;
        event->connection_id=c->connection_id; event->generation=c->generation; event->grant_id=c->grant_id;
        event->connection=c; event->peer=c->peer;
        if (c->closed_event)
        {
            event->kind=2; event->reason=(uint32_t)-c->fatal_code;
            c->closed_event=0; c->established_event=0;
        }
        else { event->kind=1; c->established_event=0; }
        return 1;
    }
    return 0;
}
int32_t gfnet_listener_confirm_binding(void* handle, uint64_t id, uint64_t generation, const uint8_t* ticket, int32_t length, uint32_t now)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if (!ticket || length<1 || length>1024) { return GFNET_INVALID_ARGUMENT; }
    if ((error=advance(l,now))) { return error; }
    gfnet_connection* c=find_connection(l,id,generation);
    if (!c || c->state!=GFNET_ESTABLISHED || c->bound) { return GFNET_INVALID_STATE; }
    gfnet_grant_slot* g=find_grant(l,c->grant_id);
    uint8_t digest[32];
    size_t digest_length=0;
    gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=c->quota;
    int status=(int)psa_hash_compute(PSA_ALG_SHA_256,ticket,(size_t)length,digest,sizeof(digest),&digest_length);
    gfnet_current_quota=previous;
    unsigned difference=0;
    if (!status && digest_length==sizeof(digest) && g) { for (int i=0; i<32; i++) { difference|=digest[i]^g->value.ticket_digest[i]; } }
    mbedtls_platform_zeroize(digest,sizeof(digest));
    if (status || digest_length!=sizeof(digest))
    { return gfnet_fault(c,status==PSA_ERROR_INSUFFICIENT_MEMORY ? GFNET_ALLOCATION_FAILED : GFNET_INTERNAL_ERROR,status); }
    if (!g || g->state!=1 || gfnet_due(now,g->expiry) || gfnet_due(now,c->hello_deadline) ||
        difference || g->value.conv!=c->conv)
    { return gfnet_fault(c,GFNET_PROTOCOL_ERROR,status); }
    g->state=2; c->bound=1;
    mbedtls_platform_zeroize(g->value.psk,32);
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* other=l->connections[i];
        if (other && other!=c && other->grant_id==c->grant_id) { gfnet_fault(other,GFNET_INVALID_STATE,0); }
    }
    return 0;
}
int32_t gfnet_listener_reject_binding(void* handle, uint64_t id, uint64_t generation)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    gfnet_connection* c=find_connection(l,id,generation);
    if (!c || c->bound) { return GFNET_INVALID_STATE; }
    gfnet_fault(c,GFNET_PROTOCOL_ERROR,0); return 0;
}
int32_t gfnet_listener_get_connection_state(void* handle, uint64_t id, uint64_t generation, int32_t* state)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    if (!state) { return GFNET_INVALID_ARGUMENT; }
    gfnet_connection* c=find_connection(l,id,generation);
    if (!c) { return GFNET_INVALID_STATE; }
    *state=c->state; return 0;
}
int32_t gfnet_listener_close_connection(void* handle, uint64_t id, uint64_t generation)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    gfnet_connection* c=find_connection(l,id,generation);
    if (!c) { return GFNET_INVALID_STATE; }
    if (c->state<GFNET_PEER_CLOSED) { c->state=GFNET_PEER_CLOSED; c->closed_event=1; }
    return 0;
}
int32_t gfnet_listener_release_connection(void* handle, uint64_t id, uint64_t generation)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    for (uint32_t i=0; i<l->slot_count; i++)
    {
        gfnet_connection* c=l->connections[i];
        if (!c || c->connection_id!=id || c->generation!=generation) { continue; }
        if (c->state<GFNET_PEER_CLOSED) { return GFNET_INVALID_STATE; }
        gfnet_grant_slot* g=find_grant(l,c->grant_id);
        int bound=c->bound;
        l->connections[i]=NULL; gfnet_connection_free(c);
        if (bound && g && g->state==2) { g->state=3; }
        return 0;
    }
    return GFNET_INVALID_STATE;
}
int32_t gfnet_listener_destroy(void* handle)
{
    gfnet_listener* l=handle; int error=check_listener(l);
    if (error) { return error; }
    gfnet_quota* quota=l->quota;
    if (l->connections)
    {
        for (uint32_t i=0; i<l->slot_count; i++) { if (l->connections[i]) { gfnet_connection_free(l->connections[i]); } }
    }
    gfnet_queue_clear(&l->cookies);
    mbedtls_ssl_cookie_free(&l->cookie);
    gfnet_free(l->connections); gfnet_free(l->grants); gfnet_free(l); gfnet_quota_destroy(quota);
    return 0;
}
