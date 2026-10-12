#include "gfnet_internal.h"
#include "mbedtls/sha256.h"
#include <stdio.h>
static int checks;
#define REQUIRE(expression) do { checks++; if (!(expression)) { fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#expression); exit(1); } } while(0)
static uint8_t identity[16]={1,2,3,4};
static uint8_t key[32]={9,8,7,6};
static gfnet_config_v1 config(uint32_t role, uint32_t conv)
{
    gfnet_config_v1 value={sizeof(value),1,role,conv,4*1024*1024,32768,1150,0,identity,16,0,key,32,0};
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
    gfnet_config_v1 cc=config(GFNET_CLIENT,42), sc=config(GFNET_SERVER,42);
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
        int32_t channel,length;
        while (gfnet_peek_frame(server,&channel,&length)==1)
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
    gfnet_stats_v1 stats={0}; stats.struct_size=sizeof(stats); stats.abi_version=1;
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
    int32_t channel, size; REQUIRE(gfnet_peek_frame(server,&channel,&size)==1);
    REQUIRE(channel==2 && size==32); REQUIRE(gfnet_recv_frame(server,received,sizeof(received))==32);
    REQUIRE(gfnet_peek_frame(server,&channel,&size)==0);
    REQUIRE(gfnet_send_unreliable(client,frame,sizeof(frame))==0);
    REQUIRE(gfnet_tick(client,((gfnet_connection*)client)->now+10)==0);
    length=gfnet_poll_udp(client,datagram,sizeof(datagram),&born); REQUIRE(length>0);
    datagram[length-1]^=1;
    REQUIRE(gfnet_feed_udp(server,datagram,length)==0);
    REQUIRE(gfnet_tick(server,((gfnet_connection*)client)->now)==0);
    REQUIRE(gfnet_peek_frame(server,&channel,&size)==0);
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
    gfnet_config_v1 cc=config(GFNET_CLIENT,42); void* handle=NULL;
    REQUIRE(gfnet_create(&cc,&handle)==0);
    gfnet_stats_v1 stats={0}; stats.struct_size=sizeof(stats); stats.abi_version=1;
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
static void step_listener(void* listener, void** clients, gfnet_peer_v1* peers, int count, uint32_t now)
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
    gfnet_udp_metadata_v1 metadata={0}; metadata.struct_size=sizeof(metadata); metadata.abi_version=1;
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
    gfnet_listener_config_v1 lc={sizeof(lc),1,256*1024*1024,4*1024*1024,4,4,8,0};
    void* listener=NULL; REQUIRE(gfnet_listener_create(&lc,&listener)==0);
    gfnet_grant_v1 grant={0}; grant.struct_size=sizeof(grant); grant.abi_version=1; grant.conv=42;
    grant.grant_id=7; grant.remaining_ttl_ms=30000;
    memcpy(grant.identity,identity,16); memcpy(grant.psk,key,32);
    uint8_t ticket[16]={4,3,2,1}; REQUIRE(mbedtls_sha256(ticket,sizeof(ticket),grant.ticket_digest,0)==0);
    REQUIRE(gfnet_listener_register_grant(listener,&grant,1000)==0);
    void* clients[2]={0}; gfnet_peer_v1 peers[2]={0}; gfnet_event_v1 events[2]={0};
    for (int i=0; i<2; i++)
    {
        gfnet_config_v1 cc=config(GFNET_CLIENT,42); REQUIRE(gfnet_create(&cc,&clients[i])==0);
        peers[i].address_family=4; peers[i].address[0]=127; peers[i].address[3]=1; peers[i].port=(uint16_t)(5000+i);
    }
    int established=0; uint32_t now=1000;
    for (; now<10000 && established<2; now+=10)
    {
        step_listener(listener,clients,peers,2,now);
        gfnet_event_v1 event={0}; event.struct_size=sizeof(event); event.abi_version=1;
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
    gfnet_event_v1 event={0}; event.struct_size=sizeof(event); event.abi_version=1;
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
static void test_wrong_psk(void)
{
    void* client=NULL; void* server=NULL;
    uint8_t wrong[32]={0};
    gfnet_config_v1 cc=config(GFNET_CLIENT,42), sc=config(GFNET_SERVER,42); cc.psk=wrong;
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
    gfnet_listener_config_v1 lc={sizeof(lc),1,256*1024*1024,4*1024*1024,4,4,8,0};
    void* listener=NULL; void* client=NULL;
    REQUIRE(gfnet_listener_create(&lc,&listener)==0);
    gfnet_config_v1 cc=config(GFNET_CLIENT,42); REQUIRE(gfnet_create(&cc,&client)==0);
    REQUIRE(gfnet_tick(client,1000)==0);
    uint8_t datagram[1200], output[1200]; uint32_t born;
    int length=gfnet_poll_udp(client,datagram,sizeof(datagram),&born); REQUIRE(length>0);
    uint64_t baseline=gfnet_memory_live();
    gfnet_peer_v1 peer={0}; peer.address_family=4; peer.address[0]=127; peer.address[3]=1;
    gfnet_udp_metadata_v1 metadata={0}; metadata.struct_size=sizeof(metadata); metadata.abi_version=1;
    for (int i=0; i<64; i++)
    {
        peer.port=(uint16_t)(5000+i);
        REQUIRE(gfnet_listener_feed_udp(listener,&peer,datagram,length,1000)==0);
        REQUIRE(gfnet_listener_poll_udp(listener,output,sizeof(output),&metadata)>0);
        REQUIRE(metadata.connection_id==0 && metadata.generation==0);
        REQUIRE(gfnet_memory_live()==baseline);
    }
    gfnet_event_v1 event={0}; event.struct_size=sizeof(event); event.abi_version=1;
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
    gfnet_config_v1 cc=config(GFNET_CLIENT,42); void* handle=NULL;
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
    gfnet_stats_v1 stats={0}; stats.struct_size=sizeof(stats); stats.abi_version=1;
    REQUIRE(gfnet_get_stats(client,&stats)==0); REQUIRE(stats.waitsnd==256);
    REQUIRE(stats.pending_reliable_bytes+stats.inflight_reliable_bytes==128*sizeof(frame));
    REQUIRE(gfnet_destroy(client)==0); REQUIRE(gfnet_destroy(server)==0); REQUIRE(gfnet_memory_live()==0);
}
int main(void)
{
    REQUIRE(gfnet_abi_version()==1);
    test_max_ack_and_window();
#if !defined(_WIN32)
    test_owner_thread();
#endif
    test_create_allocation_faults(); test_wrong_psk(); test_authenticated_trailing_kcp();
    test_output_age_and_early_tick(); test_stateless_sources();
    test_pair(1000); test_pair(UINT32_MAX-50);
    test_authenticated_bad_channel_and_replay(); test_send_allocation_fault(); test_listener();
    printf("gfnet regression: %d checks passed; live native bytes = %llu\n",checks,(unsigned long long)gfnet_memory_live());
    return 0;
}
