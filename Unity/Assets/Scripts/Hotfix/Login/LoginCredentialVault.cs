using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Game.Hotfix
{
    public interface ILoginCredentialVault
    {
        byte[] Read(string key);
        void Write(string key, byte[] value);
        void Delete(string key);
    }

    /// <summary>账号恢复凭据的系统存储；不保存密码，也不回退到明文文件。</summary>
    public sealed class LoginCredentialVault : ILoginCredentialVault
    {
        private const int PayloadSize = 64;

        public byte[] Read(string key)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            IntPtr item;
            IntPtr data;
            uint length;
            int status = FindMac(key, out length, out data, out item);
            if (status == -25300)
            {
                return null;
            }

            CheckStatus(status);
            try
            {
                if (length != PayloadSize)
                {
                    throw new InvalidOperationException("保存的登录凭据格式无效。");
                }

                var value = new byte[PayloadSize];
                Marshal.Copy(data, value, 0, value.Length);
                return value;
            }
            finally
            {
                ClearNative(data, checked((int)length));
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CFRelease(item);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            var value = new byte[PayloadSize];
            int status = GFLoginRead(key, value, value.Length);
            if (status == -25300)
            {
                return null;
            }

            CheckStatus(status);
            return value;
#elif UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.gameframe.login.LoginVault");
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            return bridge.CallStatic<byte[]>("read", activity, key);
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            IntPtr pointer;
            if (!CredRead(key, 1, 0, out pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1168)
                {
                    return null;
                }

                CheckStatus(error);
            }

            var credential = Marshal.PtrToStructure<WindowsCredential>(pointer);
            try
            {
                if (credential.BlobSize != PayloadSize)
                {
                    throw new InvalidOperationException("保存的登录凭据格式无效。");
                }

                var value = new byte[PayloadSize];
                Marshal.Copy(credential.Blob, value, 0, value.Length);
                return value;
            }
            finally
            {
                ClearNative(credential.Blob, checked((int)credential.BlobSize));
                CredFree(pointer);
            }
#else
            throw new PlatformNotSupportedException("此平台尚未接入登录凭据安全存储。");
#endif
        }

        public void Write(string key, byte[] value)
        {
            if (value == null || value.Length != PayloadSize)
            {
                throw new ArgumentException("登录凭据长度无效。", nameof(value));
            }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            IntPtr item;
            IntPtr data;
            uint length;
            int status = FindMac(key, out length, out data, out item);
            if (status == -25300)
            {
                byte[] service = Encoding.UTF8.GetBytes(Service);
                byte[] account = Encoding.UTF8.GetBytes(key);
                CheckStatus(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                    (uint)account.Length, account, (uint)value.Length, value, out item));
                CFRelease(item);
                return;
            }

            CheckStatus(status);
            try
            {
                ClearNative(data, checked((int)length));
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CheckStatus(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)value.Length, value));
            }
            finally
            {
                CFRelease(item);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            CheckStatus(GFLoginWrite(key, value, value.Length));
#elif UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.gameframe.login.LoginVault");
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            bridge.CallStatic("write", activity, key, value);
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                var credential = new WindowsCredential
                {
                    Type = 1,
                    TargetName = key,
                    BlobSize = (uint)value.Length,
                    Blob = pinned.AddrOfPinnedObject(),
                    Persist = 2,
                    UserName = "GameFrame"
                };
                if (!CredWrite(ref credential, 0))
                {
                    CheckStatus(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                pinned.Free();
            }
#else
            throw new PlatformNotSupportedException("此平台尚未接入登录凭据安全存储。");
#endif
        }

        public void Delete(string key)
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            IntPtr item;
            IntPtr data;
            uint length;
            int status = FindMac(key, out length, out data, out item);
            if (status == -25300)
            {
                return;
            }

            CheckStatus(status);
            try
            {
                ClearNative(data, checked((int)length));
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CheckStatus(SecKeychainItemDelete(item));
            }
            finally
            {
                CFRelease(item);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            int status = GFLoginDelete(key);
            if (status != -25300)
            {
                CheckStatus(status);
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.gameframe.login.LoginVault");
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            bridge.CallStatic("delete", activity, key);
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (!CredDelete(key, 1, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1168)
                {
                    CheckStatus(error);
                }
            }
#else
            throw new PlatformNotSupportedException("此平台尚未接入登录凭据安全存储。");
#endif
        }

        private static void CheckStatus(int status)
        {
            if (status != 0)
            {
                throw new InvalidOperationException("系统登录凭据存储失败，状态码：" + status);
            }
        }

        private static void ClearNative(IntPtr pointer, int length)
        {
            if (pointer != IntPtr.Zero && length > 0)
            {
                Marshal.Copy(new byte[length], 0, pointer, length);
            }
        }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static string Service => "GameFrame.Login." + Application.identifier;

        private static int FindMac(string key, out uint length, out IntPtr data, out IntPtr item)
        {
            byte[] service = Encoding.UTF8.GetBytes(Service);
            byte[] account = Encoding.UTF8.GetBytes(key);
            return SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                (uint)account.Length, account, out length, out data, out item);
        }

        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, out uint length, out IntPtr data, out IntPtr item);
        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, uint length, byte[] data, out IntPtr item);
        [DllImport(Security)]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
        [DllImport(Security)]
        private static extern int SecKeychainItemDelete(IntPtr item);
        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr value);
#elif UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int GFLoginRead(string key, [Out] byte[] value, int length);
        [DllImport("__Internal")]
        private static extern int GFLoginWrite(string key, byte[] value, int length);
        [DllImport("__Internal")]
        private static extern int GFLoginDelete(string key);
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowsCredential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public uint BlobSize;
            public IntPtr Blob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref WindowsCredential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr credential);
#endif
    }
}
