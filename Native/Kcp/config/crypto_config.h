#ifndef GFNET_CRYPTO_CONFIG_H
#define GFNET_CRYPTO_CONFIG_H
#define MBEDTLS_PLATFORM_C
#define MBEDTLS_PLATFORM_MEMORY
#define MBEDTLS_PSA_CRYPTO_C
#define MBEDTLS_PSA_CRYPTO_EXTERNAL_RNG
#define MBEDTLS_THREADING_C
#if defined(_WIN32)
#define MBEDTLS_THREADING_ALT
#else
#define MBEDTLS_THREADING_PTHREAD
#endif
/* Fixed shared slot metadata; key bytes and operation buffers still use the
 * calling connection/listener quota. No dynamically growing shared key table. */
#define MBEDTLS_PSA_KEY_SLOT_COUNT 1024
#define PSA_WANT_ALG_SHA_256 1
#define PSA_WANT_ALG_HMAC 1
#define PSA_WANT_ALG_GCM 1
#define PSA_WANT_ALG_TLS12_PRF 1
#define PSA_WANT_ALG_TLS12_PSK_TO_MS 1
#define PSA_WANT_KEY_TYPE_AES 1
#define PSA_WANT_KEY_TYPE_HMAC 1
#define PSA_WANT_KEY_TYPE_DERIVE 1
#define PSA_WANT_KEY_TYPE_RAW_DATA 1
#endif
