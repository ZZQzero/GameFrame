#ifndef GFNET_THREADING_ALT_H
#define GFNET_THREADING_ALT_H
#include <windows.h>
typedef SRWLOCK mbedtls_platform_mutex_t;
typedef CONDITION_VARIABLE mbedtls_platform_condition_variable_t;
#endif
