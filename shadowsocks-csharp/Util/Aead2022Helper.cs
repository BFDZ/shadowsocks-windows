using System;
using System.Security.Cryptography;

namespace Shadowsocks.Util
{
    public static class Aead2022Helper
    {
        public static bool IsAead2022Method(string method)
        {
            if (string.IsNullOrEmpty(method))
                return false;
            return method.StartsWith("2022-blake3-");
        }

        public static bool ValidateAead2022Password(string method, string password)
        {
            if (!IsAead2022Method(method))
                return true;

            byte[] key;
            try
            {
                key = Convert.FromBase64String(password);
            }
            catch
            {
                return false;
            }

            if (method == "2022-blake3-aes-128-gcm")
                return key.Length == 16;

            if (method == "2022-blake3-aes-256-gcm")
                return key.Length == 32;

            if (method == "2022-blake3-chacha20-poly1305")
                return key.Length == 32;

            return false;
        }

        public static int GetRequiredKeyLength(string method)
        {
            if (method == "2022-blake3-aes-128-gcm")
                return 16;
            if (method == "2022-blake3-aes-256-gcm")
                return 32;
            if (method == "2022-blake3-chacha20-poly1305")
                return 32;
            return 0;
        }

        public static string GenerateBase64Key(int length)
        {
            byte[] key = new byte[length];
            RandomNumberGenerator.Fill(key);
            return Convert.ToBase64String(key);
        }

        public static string GenerateKeyForMethod(string method)
        {
            int keyLen = GetRequiredKeyLength(method);
            if (keyLen == 0)
                return null;
            return GenerateBase64Key(keyLen);
        }
    }
}
