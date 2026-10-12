#include "gfnet_internal.h"
#include <stdio.h>
#if defined(_WIN32)
static LONG checks;
#define COUNT_CHECK() InterlockedIncrement(&checks)
#else
#include <stdatomic.h>
static _Atomic int checks;
#define COUNT_CHECK() atomic_fetch_add(&checks,1)
#endif
#define REQUIRE(expression) do { COUNT_CHECK(); if (!(expression)) { fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#expression); exit(1); } } while(0)
static uint8_t identity[16]={1,2,3,4};
static uint8_t key[32]={9,8,7,6};
static gfnet_config_v2 config(uint32_t role, uint32_t conv)
{
    gfnet_config_v2 value={sizeof(value),GFNET_ABI_VERSION,role,conv,4*1024*1024,32768,1150,0,identity,16,0,key,32,0};
    return value;
}
static void transfer(void* from, void* to)
{
    uint8_t datagram[1200]; uint32_t born;
    for (int i=0; i<256; i++)
    {
        int length=gfnet_poll_udp(from,datagram,sizeof(datagram),&born);
        if (length<=0) { REQUIRE(length==0 || length==GFNET_PROTOCOL_ERROR); break; }
        REQUIRE(length<=1200);
        REQUIRE(gfnet_feed_udp(to,datagram,length)==0);
    }
}
static void step_pair(void* client, void* server, uint32_t now)
{
    REQUIRE(gfnet_tick(client,now)==0); transfer(client,server);
    REQUIRE(gfnet_tick(server,now)==0); transfer(server,client);
}
static void connect_pair(void** client, void** server, uint32_t start)
{
    gfnet_config_v2 cc=config(GFNET_CLIENT,42), sc=config(GFNET_SERVER,42);
    REQUIRE(gfnet_create(&cc,client)==0); REQUIRE(gfnet_create(&sc,server)==0);
    REQUIRE(gfnet_tick(*server,start)==0);
    for (uint32_t elapsed=0; elapsed<10000; elapsed+=10)
    {
        step_pair(*client,*server,start+elapsed);
        int cs,ss; REQUIRE(gfnet_get_state(*client,&cs)==0); REQUIRE(gfnet_get_state(*server,&ss)==0);
        if (cs==GFNET_ESTABLISHED && ss==GFNET_ESTABLISHED) { return; }
    }
    REQUIRE(0 && "DTLS pair handshake timeout");
}
static void test_psa_rng_and_hash(void)
{
    REQUIRE(gfnet_initialize()==0);
    REQUIRE(gfnet_memory_live()==0);
    /* Public PSA entry points copy their buffers through the quota allocator. */
    gfnet_quota* quota=gfnet_quota_create(65536,NULL); REQUIRE(quota!=NULL);
    gfnet_current_quota=quota;
    uint8_t output[600]; memset(output,0xA5,sizeof(output));
    REQUIRE(psa_generate_random(output,sizeof(output))==PSA_SUCCESS);
    REQUIRE(quota->bytes==sizeof(*quota));
    gfnet_test_rng_failure=1;
    REQUIRE(psa_generate_random(output,sizeof(output))==PSA_ERROR_INSUFFICIENT_ENTROPY);
    for (size_t i=0; i<sizeof(output); i++) { REQUIRE(output[i]==0); }
    gfnet_config_v2 sc=config(GFNET_SERVER,42); void* server=(void*)(uintptr_t)1;
    REQUIRE(gfnet_create(&sc,&server)==GFNET_INTERNAL_ERROR);
    REQUIRE(server==NULL); REQUIRE(quota->bytes==sizeof(*quota));
    gfnet_test_rng_failure=0;
    REQUIRE(psa_generate_random(output,sizeof(output))==PSA_SUCCESS);
    static const uint8_t expected[32]={0xba,0x78,0x16,0xbf,0x8f,0x01,0xcf,0xea,
        0x41,0x41,0x40,0xde,0x5d,0xae,0x22,0x23,0xb0,0x03,0x61,0xa3,0x96,0x17,0x7a,0x9c,
        0xb4,0x10,0xff,0x61,0xf2,0x00,0x15,0xad};
    size_t length=0;
    REQUIRE(psa_hash_compute(PSA_ALG_SHA_256,(const uint8_t*)"abc",3,output,sizeof(output),&length)==PSA_SUCCESS);
    REQUIRE(length==sizeof(expected) && memcmp(output,expected,sizeof(expected))==0);
    mbedtls_platform_zeroize(output,sizeof(output));
    gfnet_current_quota=NULL; gfnet_quota_destroy(quota);
    REQUIRE(gfnet_memory_live()==0);
}
static void ticket_digest(const uint8_t* ticket, size_t size, uint8_t* digest)
{
    gfnet_quota* quota=gfnet_quota_create(65536,NULL); REQUIRE(quota!=NULL);
    gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=quota;
    size_t length=0;
    REQUIRE(psa_hash_compute(PSA_ALG_SHA_256,ticket,size,digest,32,&length)==PSA_SUCCESS);
    REQUIRE(length==32);
    gfnet_current_quota=previous; gfnet_quota_destroy(quota);
}
static void test_psa_slot_exhaustion(void)
{
    gfnet_quota* quota=gfnet_quota_create(4*1024*1024,NULL); REQUIRE(quota!=NULL);
    gfnet_current_quota=quota;
    psa_key_id_t slots[MBEDTLS_PSA_KEY_SLOT_COUNT]={0};
    psa_key_attributes_t attributes=PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_type(&attributes,PSA_KEY_TYPE_HMAC);
    psa_set_key_usage_flags(&attributes,PSA_KEY_USAGE_SIGN_MESSAGE);
    psa_set_key_algorithm(&attributes,PSA_ALG_HMAC(PSA_ALG_SHA_256));
    for (size_t i=0; i<MBEDTLS_PSA_KEY_SLOT_COUNT; i++)
    { REQUIRE(psa_import_key(&attributes,key,sizeof(key),&slots[i])==PSA_SUCCESS); }
    psa_key_id_t extra=0;
    REQUIRE(psa_import_key(&attributes,key,sizeof(key),&extra)==PSA_ERROR_INSUFFICIENT_MEMORY);
    REQUIRE(extra==0);
    for (size_t i=0; i<MBEDTLS_PSA_KEY_SLOT_COUNT; i++) { REQUIRE(psa_destroy_key(slots[i])==PSA_SUCCESS); }
    REQUIRE(psa_import_key(&attributes,key,sizeof(key),&extra)==PSA_SUCCESS);
    REQUIRE(psa_destroy_key(extra)==PSA_SUCCESS);
    psa_reset_key_attributes(&attributes);
    REQUIRE(quota->bytes==sizeof(*quota));
    gfnet_current_quota=NULL; gfnet_quota_destroy(quota);
    REQUIRE(gfnet_memory_live()==0);
}
static mbedtls_threading_mutex_t parallel_gate;
static mbedtls_threading_condition_variable_t parallel_ready;
static int parallel_owners;
#if defined(_WIN32)
static DWORD WINAPI parallel_owner(void* context)
#else
static void* parallel_owner(void* context)
#endif
{
    (void)context;
    void* clients[16]={0}; void* servers[16]={0};
    for (int i=0; i<16; i++) { connect_pair(&clients[i],&servers[i],1000); }
    REQUIRE(mbedtls_mutex_lock(&parallel_gate)==0);
    parallel_owners++;
    REQUIRE(mbedtls_condition_variable_broadcast(&parallel_ready)==0);
    while (parallel_owners<4) { REQUIRE(mbedtls_condition_variable_wait(&parallel_ready,&parallel_gate)==0); }
    REQUIRE(mbedtls_mutex_unlock(&parallel_gate)==0);
    for (int i=0; i<16; i++)
    {
        uint8_t frame[32]={0}, reply[32]; frame[0]=(uint8_t)i;
        REQUIRE(gfnet_send_unreliable(clients[i],frame,sizeof(frame))==0);
        step_pair(clients[i],servers[i],((gfnet_connection*)clients[i])->now+10);
        int32_t channel,length; uint32_t born;
        REQUIRE(gfnet_peek_frame(servers[i],&channel,&length,&born)==1);
        REQUIRE(channel==2 && length==sizeof(frame));
        REQUIRE(gfnet_recv_frame(servers[i],reply,sizeof(reply))==sizeof(reply));
        REQUIRE(memcmp(frame,reply,sizeof(frame))==0);
        REQUIRE(gfnet_destroy(clients[i])==0); REQUIRE(gfnet_destroy(servers[i])==0);
    }
    return 0;
}
static void test_parallel_psa_owners(void)
{
    mbedtls_mutex_init(&parallel_gate);
    REQUIRE(mbedtls_condition_variable_init(&parallel_ready)==0);
#if defined(_WIN32)
    HANDLE threads[4];
    for (int i=0; i<4; i++) { threads[i]=CreateThread(NULL,0,parallel_owner,NULL,0,NULL); REQUIRE(threads[i]!=NULL); }
    REQUIRE(WaitForMultipleObjects(4,threads,TRUE,INFINITE)==WAIT_OBJECT_0);
    for (int i=0; i<4; i++) { REQUIRE(CloseHandle(threads[i])); }
#else
    pthread_t threads[4];
    for (int i=0; i<4; i++) { REQUIRE(pthread_create(&threads[i],NULL,parallel_owner,NULL)==0); }
    for (int i=0; i<4; i++) { REQUIRE(pthread_join(threads[i],NULL)==0); }
#endif
    mbedtls_condition_variable_free(&parallel_ready); mbedtls_mutex_free(&parallel_gate);
    REQUIRE(gfnet_memory_live()==0);
}
static void test_dtls_failure_categories(void)
{
    const int causes[] = {MBEDTLS_ERR_SSL_INVALID_RECORD, MBEDTLS_ERR_SSL_INTERNAL_ERROR,
        MBEDTLS_ERR_SSL_ALLOC_FAILED, MBEDTLS_ERR_SSL_BAD_INPUT_DATA,
        MBEDTLS_ERR_SSL_HANDSHAKE_FAILURE, PSA_ERROR_INSUFFICIENT_MEMORY,
        PSA_ERROR_INVALID_SIGNATURE, PSA_ERROR_INSUFFICIENT_ENTROPY};
    const int categories[] = {GFNET_PROTOCOL_ERROR, GFNET_INTERNAL_ERROR,
        GFNET_ALLOCATION_FAILED, GFNET_INTERNAL_ERROR, GFNET_PROTOCOL_ERROR,
        GFNET_ALLOCATION_FAILED, GFNET_PROTOCOL_ERROR, GFNET_INTERNAL_ERROR};
    for (size_t i=0; i<sizeof(causes)/sizeof(causes[0]); i++)
    {
        void* handle=NULL; gfnet_config_v2 value=config(GFNET_CLIENT,42);
        REQUIRE(gfnet_create(&value,&handle)==0);
        gfnet_connection* connection=handle;
        REQUIRE(gfnet_dtls_fault(connection,causes[i])==categories[i]);
        REQUIRE(connection->native_error==causes[i]);
        REQUIRE(gfnet_dtls_fault(connection,MBEDTLS_ERR_SSL_INTERNAL_ERROR)==categories[i]);
        REQUIRE(connection->native_error==causes[i]);
        REQUIRE(gfnet_destroy(handle)==0);
    }
    void* handle=NULL; gfnet_config_v2 value=config(GFNET_CLIENT,42);
    REQUIRE(gfnet_create(&value,&handle)==0);
    gfnet_connection* connection=handle;
    connection->quota->allocation_failed=1;
    REQUIRE(gfnet_dtls_fault(connection,MBEDTLS_ERR_SSL_INTERNAL_ERROR)==GFNET_ALLOCATION_FAILED);
    REQUIRE(gfnet_destroy(handle)==0);
    REQUIRE(gfnet_memory_live()==0);
}
static void test_pair(uint32_t start)
{
    void* client=NULL; void* server=NULL;
    connect_pair(&client,&server,start);
    uint8_t frame[32768], received[32768];
    for (size_t i=0; i<sizeof(frame); i++) { frame[i]=(uint8_t)i; }
    REQUIRE(gfnet_send_reliable(client,frame,sizeof(frame))==0);
    REQUIRE(gfnet_send_unreliable(client,frame,1150)==0);
    int reliable=0,unreliable=0;
    for (uint32_t elapsed=10000; elapsed<30000 && (!reliable || !unreliable); elapsed+=10)
    {
        /* Advance from the actual handshake clock, never leave queued output idle. */
        uint32_t now=((gfnet_connection*)client)->now+10;
        step_pair(client,server,now);
        int32_t channel,length; uint32_t frame_born;
        while (gfnet_peek_frame(server,&channel,&length,&frame_born)==1)
        {
            REQUIRE(gfnet_peek_frame_header(server,received,31)==GFNET_BUFFER_TOO_SMALL);
            REQUIRE(gfnet_peek_frame_header(server,received,32)==32);
            REQUIRE(memcmp(received,frame,32)==0);
            REQUIRE(gfnet_recv_frame(server,received,1)==GFNET_BUFFER_TOO_SMALL);
            REQUIRE(gfnet_recv_frame(server,received,sizeof(received))==length);
            REQUIRE(memcmp(received,frame,(size_t)length)==0);
            if (channel==1) { REQUIRE(length==32768); reliable++; }
            else { REQUIRE(channel==2 && length==1150); unreliable++; }
        }
    }
    REQUIRE(reliable==1 && unreliable==1);
    gfnet_stats_v2 stats={0}; stats.struct_size=sizeof(stats); stats.abi_version=GFNET_ABI_VERSION;
    REQUIRE(gfnet_get_stats(client,&stats)==0);
    REQUIRE(stats.owned_bytes<=4*1024*1024 && stats.peak_bytes>=stats.owned_bytes);
    REQUIRE(stats.udp_sent_bytes>0 && stats.udp_received_bytes>0);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
static void test_authenticated_bad_channel_and_replay(void)
{
    void* client=NULL; void* server=NULL; connect_pair(&client,&server,1000);
    uint8_t frame[32]={0}, datagram[1200], received[32]; uint32_t born;
    REQUIRE(gfnet_send_unreliable(client,frame,sizeof(frame))==0);
    REQUIRE(gfnet_tick(client,((gfnet_connection*)client)->now+10)==0);
    int length=gfnet_poll_udp(client,datagram,sizeof(datagram),&born); REQUIRE(length>0);
    REQUIRE(gfnet_feed_udp(server,datagram,length)==0);
    REQUIRE(gfnet_feed_udp(server,datagram,length)==0);
    datagram[length-1]^=1; REQUIRE(gfnet_feed_udp(server,datagram,length)==0);
    REQUIRE(gfnet_tick(server,((gfnet_connection*)client)->now)==0);
    int32_t channel, size; REQUIRE(gfnet_peek_frame(server,&channel,&size,&born)==1);
    REQUIRE(channel==2 && size==32); REQUIRE(gfnet_recv_frame(server,received,sizeof(received))==32);
    REQUIRE(gfnet_peek_frame(server,&channel,&size,&born)==0);
    REQUIRE(gfnet_send_unreliable(client,frame,sizeof(frame))==0);
    REQUIRE(gfnet_tick(client,((gfnet_connection*)client)->now+10)==0);
    length=gfnet_poll_udp(client,datagram,sizeof(datagram),&born); REQUIRE(length>0);
    datagram[length-1]^=1;
    REQUIRE(gfnet_feed_udp(server,datagram,length)==0);
    REQUIRE(gfnet_tick(server,((gfnet_connection*)client)->now)==0);
    REQUIRE(gfnet_peek_frame(server,&channel,&size,&born)==0);
    datagram[length-1]^=1;
    REQUIRE(gfnet_feed_udp(server,datagram,length)==0);
    REQUIRE(gfnet_tick(server,((gfnet_connection*)client)->now+1)==0);
    REQUIRE(gfnet_recv_frame(server,received,sizeof(received))==32);
    gfnet_connection* c=client; gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=c->quota;
    uint8_t bad[33]={9}; REQUIRE(mbedtls_ssl_write(&c->ssl,bad,sizeof(bad))==sizeof(bad)); gfnet_current_quota=previous;
    transfer(client,server);
    REQUIRE(gfnet_tick(server,c->now+10)==GFNET_PROTOCOL_ERROR);
    REQUIRE(gfnet_send_reliable(server,frame,sizeof(frame))==GFNET_PROTOCOL_ERROR);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
static void test_create_allocation_faults(void)
{
    gfnet_config_v2 cc=config(GFNET_CLIENT,42); void* handle=NULL;
    REQUIRE(gfnet_create(&cc,&handle)==0);
    gfnet_stats_v2 stats={0}; stats.struct_size=sizeof(stats); stats.abi_version=GFNET_ABI_VERSION;
    REQUIRE(gfnet_get_stats(handle,&stats)==0);
    uint64_t allocations=stats.allocations;
    REQUIRE(gfnet_destroy(handle)==0);
    for (uint64_t i=0; i<allocations; i++)
    {
        gfnet_test_fail_after=(int64_t)i;
        handle=(void*)(uintptr_t)1;
        REQUIRE(gfnet_create(&cc,&handle)==GFNET_ALLOCATION_FAILED);
        REQUIRE(handle==NULL); REQUIRE(gfnet_memory_live()==0);
    }
    gfnet_test_fail_after=-1;
}
static void test_send_allocation_fault(void)
{
    void* client=NULL; void* server=NULL; connect_pair(&client,&server,1000);
    gfnet_connection* c=client; c->quota->fail_after=1;
    uint8_t frame[32768]={0}; REQUIRE(gfnet_send_reliable(client,frame,sizeof(frame))==GFNET_ALLOCATION_FAILED);
    REQUIRE(c->state==GFNET_FATAL); REQUIRE(ikcp_waitsnd(c->kcp)==1);
    REQUIRE(gfnet_send_reliable(client,frame,sizeof(frame))==GFNET_ALLOCATION_FAILED);
    REQUIRE(gfnet_tick(client,c->now+10)==GFNET_ALLOCATION_FAILED);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
static void step_listener(void* listener, void** clients, gfnet_peer_v2* peers, int count, uint32_t now)
{
    uint8_t datagram[1200]; uint32_t born;
    for (int i=0; i<count; i++)
    {
        REQUIRE(gfnet_tick(clients[i],now)==0);
        for (int j=0; j<256; j++)
        {
            int length=gfnet_poll_udp(clients[i],datagram,sizeof(datagram),&born);
            REQUIRE(length>=0); if (!length) { break; }
            REQUIRE(gfnet_listener_feed_udp(listener,&peers[i],datagram,length,now)==0);
        }
    }
    REQUIRE(gfnet_listener_tick(listener,now)==0);
    gfnet_udp_metadata_v2 metadata={0}; metadata.struct_size=sizeof(metadata); metadata.abi_version=GFNET_ABI_VERSION;
    for (int j=0; j<512; j++)
    {
        int length=gfnet_listener_poll_udp(listener,datagram,sizeof(datagram),&metadata);
        REQUIRE(length>=0); if (!length) { break; }
        int target=-1;
        for (int i=0; i<count; i++) { if (memcmp(&metadata.peer,&peers[i],sizeof(peers[i]))==0) { target=i; } }
        REQUIRE(target>=0); REQUIRE(gfnet_feed_udp(clients[target],datagram,length)==0);
    }
}
static void test_listener(void)
{
    gfnet_listener_config_v2 lc={sizeof(lc),GFNET_ABI_VERSION,256*1024*1024,4*1024*1024,4,4,8,0};
    void* listener=NULL; REQUIRE(gfnet_listener_create(&lc,&listener)==0);
    gfnet_grant_v2 grant={0}; grant.struct_size=sizeof(grant); grant.abi_version=GFNET_ABI_VERSION; grant.conv=42;
    grant.grant_id=7; grant.remaining_ttl_ms=30000;
    memcpy(grant.identity,identity,16); memcpy(grant.psk,key,32);
    uint8_t ticket[16]={4,3,2,1};
    ticket_digest(ticket,sizeof(ticket),grant.ticket_digest);
    REQUIRE(gfnet_listener_register_grant(listener,&grant,1000)==0);
    void* clients[2]={0}; gfnet_peer_v2 peers[2]={0}; gfnet_event_v2 events[2]={0};
    for (int i=0; i<2; i++)
    {
        gfnet_config_v2 cc=config(GFNET_CLIENT,42); REQUIRE(gfnet_create(&cc,&clients[i])==0);
        peers[i].address_family=4; peers[i].address[0]=127; peers[i].address[3]=1; peers[i].port=(uint16_t)(5000+i);
    }
    int established=0; uint32_t now=1000;
    for (; now<10000 && established<2; now+=10)
    {
        step_listener(listener,clients,peers,2,now);
        gfnet_event_v2 event={0}; event.struct_size=sizeof(event); event.abi_version=GFNET_ABI_VERSION;
        while (gfnet_listener_poll_event(listener,&event)==1)
        {
            REQUIRE(event.kind==1 && event.grant_id==7); events[established++]=event;
        }
    }
    REQUIRE(established==2);
    REQUIRE(gfnet_tick(events[0].connection,now)==GFNET_INVALID_STATE);
    REQUIRE(gfnet_destroy(events[0].connection)==GFNET_INVALID_STATE);
    REQUIRE(gfnet_listener_confirm_binding(listener,events[0].connection_id,events[0].generation,ticket,sizeof(ticket),now)==0);
    REQUIRE(gfnet_listener_confirm_binding(listener,events[1].connection_id,events[1].generation,ticket,sizeof(ticket),now)==GFNET_INVALID_STATE);
    REQUIRE(gfnet_listener_revoke_grant(listener,7)==0);
    int closed=0;
    gfnet_event_v2 event={0}; event.struct_size=sizeof(event); event.abi_version=GFNET_ABI_VERSION;
    while (gfnet_listener_poll_event(listener,&event)==1)
    {
        REQUIRE(event.kind==2); closed++;
        REQUIRE(gfnet_listener_release_connection(listener,event.connection_id,event.generation)==0);
        REQUIRE(gfnet_listener_release_connection(listener,event.connection_id,event.generation)==GFNET_INVALID_STATE);
    }
    REQUIRE(closed==2);
    REQUIRE(gfnet_destroy(clients[0])==0); REQUIRE(gfnet_destroy(clients[1])==0);
    REQUIRE(gfnet_listener_destroy(listener)==0); REQUIRE(gfnet_memory_live()==0);
}
static void test_receive_fairness_and_birth(uint32_t start)
{
    void* client=NULL; void* server=NULL;
    connect_pair(&client,&server,start);
    uint8_t frame[32]={0}, received[32];
    for (int i=0; i<32; i++) { REQUIRE(gfnet_send_reliable(client,frame,32)==0); }
    for (int i=0; i<8; i++) { REQUIRE(gfnet_send_unreliable(client,frame,32)==0); }
    gfnet_connection* s=server;
    for (int i=0; i<30; i++) { step_pair(client,server,((gfnet_connection*)client)->now+10); }
    REQUIRE(s->unreliable.count==8 && s->kcp->nrcv_que==32);
    uint32_t original=s->unreliable.head->born;
    for (int i=0; i<20; i++)
    {
        int32_t channel,length,again_channel,again_length; uint32_t born,again_born;
        REQUIRE(gfnet_peek_frame(server,&channel,&length,&born)==1);
        REQUIRE(channel==(i<16 ? 1 : 2) && length==32);
        REQUIRE(gfnet_peek_frame_header(server,received,32)==32);
        REQUIRE(gfnet_recv_frame(server,received,31)==GFNET_BUFFER_TOO_SMALL);
        REQUIRE(gfnet_peek_frame(server,&again_channel,&again_length,&again_born)==1);
        REQUIRE(channel==again_channel && length==again_length && born==again_born);
        if (channel==2) { REQUIRE(born==original && (uint32_t)(s->now-born)>=250); }
        REQUIRE(gfnet_recv_frame(server,received,32)==32);
    }
    int32_t channel,length; uint32_t born;
    REQUIRE(gfnet_peek_frame(server,&channel,&length,&born)==1 && channel==1);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}

static void test_listener_dtls_close_drain(void)
{
    gfnet_listener_config_v2 lc={sizeof(lc),GFNET_ABI_VERSION,256*1024*1024,4*1024*1024,4,4,8,0};
    void* listener=NULL; REQUIRE(gfnet_listener_create(&lc,&listener)==0);
    gfnet_grant_v2 grant={0}; grant.struct_size=sizeof(grant); grant.abi_version=GFNET_ABI_VERSION;
    grant.conv=42; grant.grant_id=11; grant.remaining_ttl_ms=30000;
    memcpy(grant.identity,identity,16); memcpy(grant.psk,key,32);
    uint8_t ticket[16]={1},frame[32]={0},received[32];
    ticket_digest(ticket,sizeof(ticket),grant.ticket_digest);
    REQUIRE(gfnet_listener_register_grant(listener,&grant,1000)==0);
    gfnet_config_v2 cc=config(GFNET_CLIENT,42); void* client=NULL;
    REQUIRE(gfnet_create(&cc,&client)==0);
    gfnet_peer_v2 peer={0}; peer.address_family=4; peer.address[0]=127; peer.address[3]=1; peer.port=5200;
    gfnet_event_v2 event={0}; event.struct_size=sizeof(event); event.abi_version=GFNET_ABI_VERSION;
    uint32_t now=1000;
    for (; now<10000; now+=10)
    {
        step_listener(listener,&client,&peer,1,now);
        if (gfnet_listener_poll_event(listener,&event)==1) { break; }
    }
    REQUIRE(event.kind==1);
    REQUIRE(gfnet_listener_confirm_binding(listener,event.connection_id,event.generation,ticket,16,now)==0);
    now+=10; step_listener(listener,&client,&peer,1,now);
    for (int i=0; i<3; i++) { frame[0]=(uint8_t)i; REQUIRE(gfnet_send_reliable(client,frame,32)==0); }
    for (int i=0; i<20; i++) { now+=10; step_listener(listener,&client,&peer,1,now); }
    gfnet_connection* c=client; gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=c->quota;
    REQUIRE(mbedtls_ssl_close_notify(&c->ssl)==0); gfnet_current_quota=previous;
    now+=10; step_listener(listener,&client,&peer,1,now);
    REQUIRE(gfnet_listener_poll_event(listener,&event)==1 && event.kind==2 && event.reason==0);
    REQUIRE(gfnet_send_reliable(event.connection,frame,32)==GFNET_INVALID_STATE);
    for (int i=0; i<3; i++)
    {
        int32_t channel,length; uint32_t born;
        REQUIRE(gfnet_peek_frame(event.connection,&channel,&length,&born)==1 && channel==1 && length==32);
        REQUIRE(gfnet_recv_frame(event.connection,received,32)==32 && received[0]==i);
    }
    REQUIRE(gfnet_listener_release_connection(listener,event.connection_id,event.generation)==0);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_listener_destroy(listener)==0); REQUIRE(gfnet_memory_live()==0);
}

static void test_wrong_psk(void)
{
    void* client=NULL; void* server=NULL;
    uint8_t wrong[32]={0};
    gfnet_config_v2 cc=config(GFNET_CLIENT,42), sc=config(GFNET_SERVER,42); cc.psk=wrong;
    REQUIRE(gfnet_create(&cc,&client)==0); REQUIRE(gfnet_create(&sc,&server)==0);
    REQUIRE(gfnet_tick(server,1000)==0);
    int failed=0;
    for (uint32_t now=1000; now<12000 && !failed; now+=10)
    {
        int cr=gfnet_tick(client,now); transfer(client,server);
        int sr=gfnet_tick(server,now); transfer(server,client);
        int cs,ss; REQUIRE(gfnet_get_state(client,&cs)==0); REQUIRE(gfnet_get_state(server,&ss)==0);
        REQUIRE(cs!=GFNET_ESTABLISHED && ss!=GFNET_ESTABLISHED);
        failed=cr<0 || sr<0;
    }
    REQUIRE(failed); REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0);
    REQUIRE(gfnet_memory_live()==0);
}
static void test_authenticated_trailing_kcp(void)
{
    void* client=NULL; void* server=NULL; connect_pair(&client,&server,1000);
    gfnet_connection* c=client; gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=c->quota;
    /* A valid ACK followed by one byte must not pass ikcp_input's permissive tail handling. */
    uint8_t bad[26]={1,42,0,0,0,82};
    REQUIRE(mbedtls_ssl_write(&c->ssl,bad,sizeof(bad))==sizeof(bad)); gfnet_current_quota=previous;
    transfer(client,server); REQUIRE(gfnet_tick(server,c->now+10)==GFNET_PROTOCOL_ERROR);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
static void test_output_age_and_early_tick(void)
{
    void* client=NULL; void* server=NULL; connect_pair(&client,&server,1000);
    uint8_t frame[32768]={0}; gfnet_connection* c=client;
    REQUIRE(gfnet_send_reliable(client,frame,sizeof(frame))==0);
    uint32_t now=c->now+10; REQUIRE(gfnet_tick(client,now)==0);
    uint32_t count=c->output.count; REQUIRE(count>0);
    REQUIRE(gfnet_tick(client,now+1)==0); REQUIRE(c->output.count==count);
    REQUIRE(gfnet_tick(client,now+251)<0); REQUIRE(c->state==GFNET_FATAL);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
static void test_stateless_sources(void)
{
    gfnet_listener_config_v2 lc={sizeof(lc),GFNET_ABI_VERSION,256*1024*1024,4*1024*1024,4,4,8,0};
    void* listener=NULL; void* client=NULL;
    REQUIRE(gfnet_listener_create(&lc,&listener)==0);
    gfnet_config_v2 cc=config(GFNET_CLIENT,42); REQUIRE(gfnet_create(&cc,&client)==0);
    REQUIRE(gfnet_tick(client,1000)==0);
    uint8_t datagram[1200], output[1200]; uint32_t born;
    int length=gfnet_poll_udp(client,datagram,sizeof(datagram),&born); REQUIRE(length>0);
    uint64_t baseline=gfnet_memory_live();
    gfnet_peer_v2 peer={0}; peer.address_family=4; peer.address[0]=127; peer.address[3]=1;
    gfnet_udp_metadata_v2 metadata={0}; metadata.struct_size=sizeof(metadata); metadata.abi_version=GFNET_ABI_VERSION;
    for (int i=0; i<64; i++)
    {
        peer.port=(uint16_t)(5000+i);
        REQUIRE(gfnet_listener_feed_udp(listener,&peer,datagram,length,1000)==0);
        REQUIRE(gfnet_listener_poll_udp(listener,output,sizeof(output),&metadata)>0);
        REQUIRE(metadata.connection_id==0 && metadata.generation==0);
        REQUIRE(gfnet_memory_live()==baseline);
    }
    gfnet_event_v2 event={0}; event.struct_size=sizeof(event); event.abi_version=GFNET_ABI_VERSION;
    REQUIRE(gfnet_listener_poll_event(listener,&event)==0);
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_listener_destroy(listener)==0); REQUIRE(gfnet_memory_live()==0);
}
#if !defined(_WIN32)
static void* wrong_owner(void* handle)
{
    int state=0;
    REQUIRE(gfnet_get_state(handle,&state)==GFNET_INVALID_STATE);
    REQUIRE(gfnet_tick(handle,1000)==GFNET_INVALID_STATE);
    REQUIRE(gfnet_destroy(handle)==GFNET_INVALID_STATE);
    return NULL;
}
static void test_owner_thread(void)
{
    gfnet_config_v2 cc=config(GFNET_CLIENT,42); void* handle=NULL;
    REQUIRE(gfnet_create(&cc,&handle)==0);
    pthread_t thread; REQUIRE(pthread_create(&thread,NULL,wrong_owner,handle)==0);
    REQUIRE(pthread_join(thread,NULL)==0); REQUIRE(gfnet_destroy(handle)==0); REQUIRE(gfnet_memory_live()==0);
}
#endif
static void test_max_ack_and_window(void)
{
    void* client=NULL; void* server=NULL; connect_pair(&client,&server,1000);
    uint8_t frame[1150]={0}, output[1200]; uint32_t born;
    for (int i=0; i<128; i++) { REQUIRE(gfnet_send_reliable(client,frame,sizeof(frame))==0); }
    gfnet_connection* c=client;
    /* The largest public window can produce an ACK list well above one MTU. */
    gfnet_connection* s=server; gfnet_quota* previous=gfnet_current_quota; gfnet_current_quota=s->quota;
    uint8_t segment[24]={42,0,0,0,81};
    for (int i=0; i<128; i++)
    {
        segment[12]=(uint8_t)i;
        REQUIRE(ikcp_input(s->kcp,(char*)segment,sizeof(segment))==0);
    }
    gfnet_current_quota=previous;
    REQUIRE(s->kcp->ackcount==128);
    uint32_t now=c->now+10;
    REQUIRE(gfnet_tick(client,now)==0); REQUIRE(gfnet_tick(server,now)==0);
    int count=0, length;
    while ((length=gfnet_poll_udp(server,output,sizeof(output),&born))>0)
    { REQUIRE(length<=1200); count++; }
    REQUIRE(length==0 && count>=3);
    gfnet_stats_v2 stats={0}; stats.struct_size=sizeof(stats); stats.abi_version=GFNET_ABI_VERSION;
    REQUIRE(gfnet_get_stats(client,&stats)==0); REQUIRE(stats.waitsnd==256);
    REQUIRE(stats.pending_reliable_bytes+stats.inflight_reliable_bytes==128*sizeof(frame));
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
int main(void)
{
    REQUIRE(gfnet_abi_version()==GFNET_ABI_VERSION);
    test_psa_rng_and_hash(); test_parallel_psa_owners();
    test_max_ack_and_window();
#if !defined(_WIN32)
    test_owner_thread();
#endif
    test_create_allocation_faults(); test_wrong_psk(); test_authenticated_trailing_kcp();
    test_output_age_and_early_tick(); test_stateless_sources();
    test_dtls_failure_categories();
    test_pair(1000); test_pair(UINT32_MAX-50);
    test_receive_fairness_and_birth(1000); test_receive_fairness_and_birth(UINT32_MAX-50);
    test_listener_dtls_close_drain();
    test_authenticated_bad_channel_and_replay(); test_send_allocation_fault(); test_listener();
    test_psa_slot_exhaustion();
    printf("gfnet regression: %d checks passed; live native bytes = %llu\n",(int)checks,(unsigned long long)gfnet_memory_live());
    return 0;
}
