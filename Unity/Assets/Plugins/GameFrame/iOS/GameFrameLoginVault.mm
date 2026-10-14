#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <cstring>

static NSMutableDictionary *LoginQuery(const char *key)
{
    NSString *service = [@"GameFrame.Login." stringByAppendingString:NSBundle.mainBundle.bundleIdentifier];
    return [@{ (__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
               (__bridge id)kSecAttrService: service,
               (__bridge id)kSecAttrAccount: [NSString stringWithUTF8String:key] } mutableCopy];
}

extern "C" int GFLoginRead(const char *key, unsigned char *output, int length)
{
    @autoreleasepool
    {
        if (key == nullptr || output == nullptr || length != 64)
        {
            return errSecParam;
        }

        NSMutableDictionary *query = LoginQuery(key);
        query[(__bridge id)kSecReturnData] = @YES;
        query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
        CFTypeRef result = nullptr;
        OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
        if (status == errSecSuccess)
        {
            NSData *data = (__bridge NSData *)result;
            if (data.length == (NSUInteger)length)
            {
                memcpy(output, data.bytes, length);
            }
            else
            {
                status = errSecDecode;
            }
        }

        if (result != nullptr)
        {
            CFRelease(result);
        }

        return status;
    }
}

extern "C" int GFLoginWrite(const char *key, const unsigned char *value, int length)
{
    @autoreleasepool
    {
        if (key == nullptr || value == nullptr || length != 64)
        {
            return errSecParam;
        }

        NSMutableDictionary *query = LoginQuery(key);
        NSMutableData *data = [NSMutableData dataWithBytes:value length:length];
        NSDictionary *attributes = @{ (__bridge id)kSecValueData: data,
            (__bridge id)kSecAttrAccessible: (__bridge id)kSecAttrAccessibleWhenUnlockedThisDeviceOnly };
        OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query, (__bridge CFDictionaryRef)attributes);
        if (status == errSecItemNotFound)
        {
            [query addEntriesFromDictionary:attributes];
            status = SecItemAdd((__bridge CFDictionaryRef)query, nullptr);
        }

        memset(data.mutableBytes, 0, data.length);
        return status;
    }
}

extern "C" int GFLoginDelete(const char *key)
{
    @autoreleasepool
    {
        if (key == nullptr)
        {
            return errSecParam;
        }

        return SecItemDelete((__bridge CFDictionaryRef)LoginQuery(key));
    }
}
