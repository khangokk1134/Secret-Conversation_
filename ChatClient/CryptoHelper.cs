using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient
{
    public static class CryptoHelper
    {
        // RSA key pair with XML (simple)
        public static void GenerateRsaKeys(out string pubXml, out string privXml, int bits = 2048)
        {
            var rsa = new RSACryptoServiceProvider(bits);
            pubXml = rsa.ToXmlString(false);
            privXml = rsa.ToXmlString(true);
        }

        public static string RsaEncryptBase64(string plain, string publicKeyXml)
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(publicKeyXml);
            var enc = rsa.Encrypt(bytes, false);
            return Convert.ToBase64String(enc);
        }

        public static string RsaDecryptBase64(string base64, string privateKeyXml)
        {
            var rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(privateKeyXml);
            var cipher = Convert.FromBase64String(base64);
            var dec = rsa.Decrypt(cipher, false);
            return Encoding.UTF8.GetString(dec);
        }

        // AES: generate key bytes (32 bytes for AES-256)
        public static byte[] GenerateAesKey(int size = 32)
        {
            var k = new byte[size];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(k);
            }
            return k;
        }

        // AES encrypt to base64, we prefix IV then cipher bytes
        public static string AesEncryptToBase64(string plain, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                using (var ms = new MemoryStream())
                {
                    // write IV first
                    ms.Write(aes.IV, 0, aes.IV.Length);
                    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs, Encoding.UTF8))
                    {
                        sw.Write(plain);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public static string AesDecryptFromBase64(string base64, byte[] key)
        {
            var data = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(data))
            {
                var iv = new byte[16];
                ms.Read(iv, 0, iv.Length);
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
        }
    }
}
