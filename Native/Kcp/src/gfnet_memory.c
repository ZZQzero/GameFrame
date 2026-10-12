#include "gfnet_internal.h"
#include <assert.h>
#include "psa/crypto_extra.h"
#if defined(_WIN32)
#include <bcrypt.h>
#elif defined(__APPLE__)
#include <CommonCrypto/CommonRandom.h>
#elif defined(__ANDROID__)
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#else
#include <errno.h>
#include <sys/random.h>
#endif
GFNET_TLS gfnet_quota* gfnet_current_quota;
#ifdef GFNET_TESTING
GFNET_TLS int64_t gfnet_test_fail_after = -1;
GFNET_TLS int gfnet_test_rng_failure;
#endif
static uint64_t global_bytes;
static int initialized;
static int initialization_error;
#if defined(_WIN32)
static SRWLOCK memory_lock = SRWLOCK_INIT;
static void lock_memory(void) { AcquireSRWLockExclusive(&memory_lock); }
static void unlock_memory(void) { ReleaseSRWLockExclusive(&memory_lock); }
uintptr_t gfnet_thread_id(void) { return (uintptr_t)GetCurrentThreadId(); }
static int crypto_mutex_init(mbedtls_platform_mutex_t* mutex) { InitializeSRWLock(mutex); return 0; }
static void crypto_mutex_destroy(mbedtls_platform_mutex_t* mutex) { (void)mutex; }
static int crypto_mutex_lock(mbedtls_platform_mutex_t* mutex) { AcquireSRWLockExclusive(mutex); return 0; }
static int crypto_mutex_unlock(mbedtls_platform_mutex_t* mutex) { ReleaseSRWLockExclusive(mutex); return 0; }
static int crypto_cond_init(mbedtls_platform_condition_variable_t* cond) { InitializeConditionVariable(cond); return 0; }
static void crypto_cond_destroy(mbedtls_platform_condition_variable_t* cond) { (void)cond; }
static int crypto_cond_signal(mbedtls_platform_condition_variable_t* cond) { WakeConditionVariable(cond); return 0; }
static int crypto_cond_broadcast(mbedtls_platform_condition_variable_t* cond) { WakeAllConditionVariable(cond); return 0; }
static int crypto_cond_wait(mbedtls_platform_condition_variable_t* cond, mbedtls_platform_mutex_t* mutex)
{
    return SleepConditionVariableSRW(cond, mutex, INFINITE, 0) ? 0 : MBEDTLS_ERR_THREADING_USAGE_ERROR;
}
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
psa_status_t mbedtls_psa_external_get_random(mbedtls_psa_external_random_context_t* context,
    uint8_t* output, size_t size, size_t* length)
{
    (void)context;
    *length = 0;
    int failed = 0;
#ifdef GFNET_TESTING
    failed = gfnet_test_rng_failure;
#endif
#if defined(_WIN32)
    if (!failed && (size > ULONG_MAX || BCryptGenRandom(NULL, output, (ULONG)size,
        BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0)) { failed = 1; }
#elif defined(__APPLE__)
    if (!failed && CCRandomGenerateBytes(output, size) != kCCSuccess) { failed = 1; }
#elif defined(__ANDROID__)
    /* Android API 23 also supports kernels/libc without getrandom/getentropy.
     * The OS is fully booted before Unity runs; use its seeded CSPRNG directly. */
    int fd = failed ? -1 : open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (fd < 0) { failed = 1; }
    for (size_t offset = 0; !failed && offset < size;)
    {
        ssize_t count = read(fd, output + offset, size - offset);
        if (count < 0 && errno == EINTR) { continue; }
        if (count <= 0) { failed = 1; break; }
        offset += (size_t)count;
    }
    if (fd >= 0 && close(fd) != 0) { failed = 1; }
#else
    for (size_t offset = 0; !failed && offset < size;)
    {
        ssize_t count = getrandom(output + offset, size - offset, 0);
        if (count < 0 && errno == EINTR) { continue; }
        if (count <= 0) { failed = 1; break; }
        offset += (size_t)count;
    }
#endif
    if (failed)
    {
        mbedtls_platform_zeroize(output, size);
        return PSA_ERROR_INSUFFICIENT_ENTROPY;
    }
    *length = size;
    return PSA_SUCCESS;
}
int gfnet_initialize(void)
{
    lock_memory();
    if (!initialized)
    {
        initialization_error = mbedtls_platform_set_calloc_free(gfnet_calloc, gfnet_free) == 0 ? 0 : GFNET_INTERNAL_ERROR;
        ikcp_allocator(gfnet_alloc, gfnet_free);
        /* PSA global slot metadata and the OS RNG have no heap state.
         * Initialization runs before a quota exists; all per-key/operation heap
         * allocations must remain owned by subsequent native calls. */
#if defined(_WIN32)
        mbedtls_threading_set_alt(crypto_mutex_init, crypto_mutex_destroy, crypto_mutex_lock, crypto_mutex_unlock,
            crypto_cond_init, crypto_cond_destroy, crypto_cond_signal, crypto_cond_broadcast, crypto_cond_wait);
#endif
        if (!initialization_error && psa_crypto_init() != PSA_SUCCESS) { initialization_error = GFNET_INTERNAL_ERROR; }
        initialized = 1;
    }
    unlock_memory();
    return initialization_error;
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
