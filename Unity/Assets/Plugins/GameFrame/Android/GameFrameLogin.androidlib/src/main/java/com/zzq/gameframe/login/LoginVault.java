package com.zzq.gameframe.login;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

public final class LoginVault
{
    private static final String ALIAS = "GameFrame.Login";

    private static SecretKey secret() throws Exception
    {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        if (store.containsAlias(ALIAS))
        {
            return (SecretKey)store.getKey(ALIAS, null);
        }

        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .build());
        return generator.generateKey();
    }

    private static AtomicFile file(Context context, String key) throws Exception
    {
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(key.getBytes(StandardCharsets.UTF_8));
        StringBuilder name = new StringBuilder("login-");
        for (byte part : hash)
        {
            name.append(String.format(java.util.Locale.ROOT, "%02x", part & 255));
        }

        return new AtomicFile(new File(context.getNoBackupFilesDir(), name.toString()));
    }

    public static byte[] read(Context context, String key) throws Exception
    {
        AtomicFile storage = file(context, key);
        if (!storage.getBaseFile().exists())
        {
            return null;
        }

        byte[] encrypted = storage.readFully();
        if (encrypted.length != 92)
        {
            throw new IOException("Invalid login credential format");
        }

        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.DECRYPT_MODE, secret(), new GCMParameterSpec(128, encrypted, 0, 12));
        cipher.updateAAD(key.getBytes(StandardCharsets.UTF_8));
        return cipher.doFinal(encrypted, 12, encrypted.length - 12);
    }

    public static void write(Context context, String key, byte[] value) throws Exception
    {
        if (value == null || value.length != 64)
        {
            throw new IllegalArgumentException("Invalid login credential length");
        }

        try
        {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, secret());
            cipher.updateAAD(key.getBytes(StandardCharsets.UTF_8));
            byte[] encrypted = cipher.doFinal(value);
            AtomicFile storage = file(context, key);
            FileOutputStream output = storage.startWrite();
            try
            {
                output.write(cipher.getIV());
                output.write(encrypted);
                storage.finishWrite(output);
            }
            catch (Exception error)
            {
                storage.failWrite(output);
                throw error;
            }
        }
        finally
        {
            Arrays.fill(value, (byte)0);
        }
    }

    public static void delete(Context context, String key) throws Exception
    {
        AtomicFile storage = file(context, key);
        storage.delete();
        if (storage.getBaseFile().exists())
        {
            throw new IOException("Login credential deletion failed");
        }
    }
}
