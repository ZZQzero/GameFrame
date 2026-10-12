#include "gfnet_internal.h"
#include <assert.h>
GFNET_TLS gfnet_quota* gfnet_current_quota;
#ifdef GFNET_TESTING
GFNET_TLS int64_t gfnet_test_fail_after = -1;
#endif
static uint64_t global_bytes;
static int initialized;
#if defined(_WIN32)
static SRWLOCK memory_lock = SRWLOCK_INIT;
static void lock_memory(void) { AcquireSRWLockExclusive(&memory_lock); }
static void unlock_memory(void) { ReleaseSRWLockExclusive(&memory_lock); }
uintptr_t gfnet_thread_id(void) { return (uintptr_t)GetCurrentThreadId(); }
#else
static pthread_mutex_t memory_lock = PTHREAD_MUTEX_INITIALIZER;
static void lock_memory(void) { pthread_mutex_lock(&memory_lock); }
static void unlock_memory(void) { pthread_mutex_unlock(&memory_lock); }
uintptr_t gfnet_thread_id(void) { return (uintptr_t)pthread_self(); }
#endif
typedef union gfnet_allocation_header {
    max_align_t alignment;
    struct { gfnet_quota* owner; size_t bytes; } value;
} gfnet_allocation_header;
static int reserve(gfnet_quota* quota, size_t bytes)
{
    gfnet_quota* current;
    if (!quota || bytes > 256ull * 1024 * 1024 - global_bytes) { return 0; }
    for (current = quota; current; current = current->parent)
    {
        if (bytes > current->limit - current->bytes) { return 0; }
    }
    global_bytes += bytes;
    for (current = quota; current; current = current->parent)
    {
        current->bytes += bytes;
        if (current->bytes > current->peak) { current->peak = current->bytes; }
    }
    return 1;
}
static void unreserve(gfnet_quota* quota, size_t bytes)
{
    gfnet_quota* current;
    assert(global_bytes >= bytes);
    global_bytes -= bytes;
    for (current = quota; current; current = current->parent)
    {
        assert(current->bytes >= bytes);
        current->bytes -= bytes;
    }
}
void gfnet_initialize(void)
{
    lock_memory();
    if (!initialized)
    {
        mbedtls_platform_set_calloc_free(gfnet_calloc, gfnet_free);
        ikcp_allocator(gfnet_alloc, gfnet_free);
        initialized = 1;
    }
    unlock_memory();
}
gfnet_quota* gfnet_quota_create(uint64_t limit, gfnet_quota* parent)
{
    gfnet_quota* quota = calloc(1, sizeof(*quota));
    if (!quota) { return NULL; }
    quota->limit = limit;
    quota->parent = parent;
    quota->fail_after = -1;
#ifdef GFNET_TESTING
    quota->fail_after = gfnet_test_fail_after;
#endif
    lock_memory();
    int ok = reserve(quota, sizeof(*quota));
    unlock_memory();
    if (!ok) { free(quota); return NULL; }
    return quota;
}
void gfnet_quota_destroy(gfnet_quota* quota)
{
    if (!quota) { return; }
    lock_memory();
    assert(quota->bytes == sizeof(*quota));
    unreserve(quota, sizeof(*quota));
    unlock_memory();
    mbedtls_platform_zeroize(quota, sizeof(*quota));
    free(quota);
}
void* gfnet_alloc(size_t size)
{
    gfnet_quota* owner = gfnet_current_quota;
    if (!owner || size > SIZE_MAX - sizeof(gfnet_allocation_header)) { return NULL; }
    size_t bytes = size + sizeof(gfnet_allocation_header);
    lock_memory();
    int ok = owner->fail_after != 0 && reserve(owner, bytes);
    if (owner->fail_after > 0) { owner->fail_after--; }
    if (!ok) { owner->allocation_failed = 1; }
    unlock_memory();
    if (!ok) { return NULL; }
    gfnet_allocation_header* header = malloc(bytes);
    if (!header)
    {
        lock_memory(); unreserve(owner, bytes); owner->allocation_failed = 1; unlock_memory();
        return NULL;
    }
    header->value.owner = owner;
    header->value.bytes = bytes;
    owner->allocations++;
    return header + 1;
}
void* gfnet_calloc(size_t count, size_t size)
{
    if (size && count > SIZE_MAX / size) { return NULL; }
    size_t bytes = count * size;
    void* memory = gfnet_alloc(bytes);
    if (memory) { memset(memory, 0, bytes); }
    return memory;
}
void gfnet_free(void* memory)
{
    if (!memory) { return; }
    gfnet_allocation_header* header = (gfnet_allocation_header*)memory - 1;
    gfnet_quota* owner = header->value.owner;
    size_t bytes = header->value.bytes;
    mbedtls_platform_zeroize(header, bytes);
    free(header);
    lock_memory(); unreserve(owner, bytes); unlock_memory();
}
uint64_t gfnet_memory_live(void)
{
    lock_memory(); uint64_t value = global_bytes; unlock_memory(); return value;
}
int gfnet_queue_push(gfnet_queue* queue, const uint8_t* data, uint32_t length, uint32_t now, uint32_t max_count, uint32_t max_bytes)
{
    uint32_t bytes = (uint32_t)(sizeof(gfnet_packet) + sizeof(gfnet_allocation_header)) + length;
    if (queue->count >= max_count || bytes > max_bytes - queue->bytes) { return GFNET_BACKPRESSURE; }
    gfnet_packet* packet = gfnet_alloc(sizeof(*packet) + length);
    if (!packet) { return GFNET_ALLOCATION_FAILED; }
    packet->next = NULL; packet->length = length; packet->born = now;
    memcpy(packet->data, data, length);
    if (queue->tail) { queue->tail->next = packet; } else { queue->head = packet; }
    queue->tail = packet; queue->count++; queue->bytes += bytes;
    return 0;
}
void gfnet_queue_pop(gfnet_queue* queue)
{
    gfnet_packet* packet = queue->head;
    if (!packet) { return; }
    queue->head = packet->next;
    if (!queue->head) { queue->tail = NULL; }
    queue->count--;
    queue->bytes -= (uint32_t)(sizeof(*packet) + sizeof(gfnet_allocation_header)) + packet->length;
    gfnet_free(packet);
}
void gfnet_queue_clear(gfnet_queue* queue)
{
    while (queue->head) { gfnet_queue_pop(queue); }
}
