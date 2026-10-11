#include "ikcp.h"
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define REQUIRE(condition) do { \
    if (!(condition)) { \
        fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); \
        exit(1); \
    } \
} while (0)

union allocation_header {
    max_align_t alignment;
    size_t size;
};

static size_t live_bytes;
static size_t live_blocks;
static int allocation_calls;
static int fail_at;
static int cases;

static void *quota_allocate(size_t size)
{
    union allocation_header *header;
    allocation_calls++;
    if (fail_at != 0 && allocation_calls == fail_at) {
        return NULL;
    }
    header = (union allocation_header *)malloc(sizeof(*header) + size);
    REQUIRE(header != NULL);
    header->size = size;
    live_bytes += size;
    live_blocks++;
    return header + 1;
}

static void quota_free(void *value)
{
    union allocation_header *header;
    if (value == NULL) {
        return;
    }
    header = ((union allocation_header *)value) - 1;
    REQUIRE(live_blocks > 0 && live_bytes >= header->size);
    live_bytes -= header->size;
    live_blocks--;
    memset(header + 1, 0, header->size);
    free(header);
}

static void inject(int index)
{
    fail_at = index;
    allocation_calls = 0;
}

static ikcpcb *create(void)
{
    ikcpcb *kcp;
    inject(0);
    kcp = ikcp_create(7, NULL);
    REQUIRE(kcp != NULL && allocation_calls == 2);
    REQUIRE(ikcp_setmtu(kcp, 1150) == 0);
    return kcp;
}

static void release(ikcpcb *kcp)
{
    if (kcp != NULL) {
        ikcp_release(kcp);
    }
    REQUIRE(live_bytes == 0 && live_blocks == 0);
    cases++;
}

static void put32(char *buffer, unsigned value)
{
    buffer[0] = (char)value;
    buffer[1] = (char)(value >> 8);
    buffer[2] = (char)(value >> 16);
    buffer[3] = (char)(value >> 24);
}

static void push(char *buffer, unsigned sequence)
{
    memset(buffer, 0, 25);
    put32(buffer, 7);
    buffer[4] = 81;
    buffer[6] = (char)128;
    put32(buffer + 8, 100);
    put32(buffer + 12, sequence);
    put32(buffer + 20, 1);
    buffer[24] = (char)(sequence + 1);
}

static void test_create(void)
{
    int index;
    for (index = 1; index <= 3; index++) {
        ikcpcb *kcp;
        inject(index == 3 ? 0 : index);
        kcp = ikcp_create(7, NULL);
        REQUIRE((index == 3) == (kcp != NULL));
        REQUIRE(allocation_calls == (index == 1 ? 1 : 2));
        release(kcp);
    }
}

static void test_mtu(void)
{
    int index;
    for (index = 0; index <= 1; index++) {
        ikcpcb *kcp = create();
        char *old_buffer = kcp->buffer;
        size_t bytes = live_bytes;
        inject(index);
        REQUIRE(ikcp_setmtu(kcp, 900) == (index == 0 ? 0 : -2));
        REQUIRE(allocation_calls == 1);
        if (index != 0) {
            REQUIRE(kcp->buffer == old_buffer && kcp->mtu == 1150);
            REQUIRE(live_bytes == bytes);
        } else {
            REQUIRE(kcp->mtu == 900);
        }
        release(kcp);
    }
}

static void test_send(int streaming)
{
    int index;
    int allocations = streaming ? 3 : 4;
    for (index = 0; index <= allocations; index++) {
        ikcpcb *kcp = create();
        char payload[4000];
        int length = streaming ? (int)kcp->mss * 2 : (int)kcp->mss * 3 + 1;
        int result;
        memset(payload, 42, sizeof(payload));
        if (streaming) {
            kcp->stream = 1;
            REQUIRE(ikcp_send(kcp, payload, 100) == 100);
        }
        inject(index);
        result = ikcp_send(kcp, payload, length);
        REQUIRE(result == (index == 0 ? length : -2));
        REQUIRE(allocation_calls == (index == 0 ? allocations : index));
        // Partial mutation is retained; the future gfnet owner must latch fatal, never retry.
        REQUIRE(kcp->nsnd_que == (unsigned)(index == 0 ? allocations :
            (streaming && index == 1 ? 1 : index - 1)));
        release(kcp);
    }
}

static void test_zero_send(void)
{
    int index;
    for (index = 0; index <= 1; index++) {
        ikcpcb *kcp = create();
        inject(index);
        REQUIRE(ikcp_send(kcp, "", 0) == (index == 0 ? 0 : -2));
        REQUIRE(allocation_calls == 1);
        release(kcp);
    }
}

static void test_input(int segments)
{
    int index;
    int allocations = segments + 1;
    for (index = 0; index <= allocations; index++) {
        ikcpcb *kcp = create();
        char packet[50];
        char decoded[2];
        push(packet, 0);
        if (segments == 2) {
            push(packet + 25, 1);
        }
        inject(index);
        REQUIRE(ikcp_input(kcp, packet, segments * 25) == (index == 0 ? 0 : -4));
        REQUIRE(allocation_calls == (index == 0 ? allocations : index));
        REQUIRE(kcp->ackcount == (unsigned)(index == 0 ? segments : (index == 1 ? 0 : index - 1)));
        REQUIRE(kcp->nrcv_que == (unsigned)(index == 0 ? segments : (index < 3 ? 0 : index - 2)));
        if (index == 0) {
            REQUIRE(ikcp_recv(kcp, decoded, sizeof(decoded)) == 1 && decoded[0] == 1);
            if (segments == 2) {
                REQUIRE(ikcp_recv(kcp, decoded, sizeof(decoded)) == 1 && decoded[0] == 2);
            }
        }
        release(kcp);
    }
}

static void test_ack_growth(void)
{
    int index;
    for (index = 0; index <= 1; index++) {
        ikcpcb *kcp = create();
        char packet[25];
        IUINT32 *old_ack;
        size_t old_bytes;
        int repeat;
        push(packet, 0);
        for (repeat = 0; repeat < 8; repeat++) {
            REQUIRE(ikcp_input(kcp, packet, sizeof(packet)) == 0);
        }
        REQUIRE(kcp->ackcount == 8 && kcp->ackblock == 8);
        old_ack = kcp->acklist;
        old_bytes = live_bytes;
        inject(index);
        REQUIRE(ikcp_input(kcp, packet, sizeof(packet)) == (index == 0 ? 0 : -4));
        REQUIRE(allocation_calls == 1 && kcp->ackcount == (unsigned)(index == 0 ? 9 : 8));
        if (index != 0) {
            REQUIRE(kcp->acklist == old_ack && kcp->ackblock == 8 && live_bytes == old_bytes);
        } else {
            REQUIRE(kcp->ackblock == 16);
        }
        for (repeat = 0; repeat < (int)kcp->ackcount; repeat++) {
            REQUIRE(kcp->acklist[repeat * 2] == 0 && kcp->acklist[repeat * 2 + 1] == 100);
        }
        release(kcp);
    }
}

int main(void)
{
    ikcp_allocator(quota_allocate, quota_free);
    test_create();
    test_mtu();
    test_send(0);
    test_send(1);
    test_zero_send();
    test_input(1);
    test_input(2);
    test_ack_growth();
    REQUIRE(cases == 25);
    printf("PASS: %d allocation failure/success cases, all owners released to zero.\n", cases);
    return 0;
}
