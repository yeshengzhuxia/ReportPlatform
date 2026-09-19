using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using ReportPlatform.Infrastructure;
using ReportPlatform.Models;

namespace ReportPlatform.Services;

public sealed class CredentialService
{
    private readonly byte[] key;
    public CredentialService(PlatformSettings settings)
    {
        if (settings.EncryptionKey.Length != 64 || !settings.EncryptionKey.All(Uri.IsHexDigit))
            throw new InvalidOperationException("请配置 Platform:EncryptionKey（64 位十六进制密钥），迁移时使用原 .env 的 ENCRYPTION_KEY。");
        key = Convert.FromHexString(settings.EncryptionKey);
    }
    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 12 || password.Length > 256)
            throw new ApiException("密码长度应为 12–256 位");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA512, 64);
        return $"pbkdf2:210000:{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
    }
    public static bool VerifyPassword(string? password, string hash)
    {
        if (password is null || password.Length > 256) return false;
        try
        {
            var parts = hash.Split(':');
            if (parts.Length == 4 && parts[0] == "pbkdf2")
            {
                var iterations = int.Parse(parts[1]);
                if (iterations is < 100_000 or > 1_000_000) return false;
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(parts[2]), iterations, HashAlgorithmName.SHA512, 64);
                return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(parts[3]));
            }
            // Original Node crypto.scryptSync used the hex salt text as UTF-8, not decoded salt bytes.
            if (parts.Length == 2 && parts[0].Length == 32 && parts[1].Length == 128)
                return CryptographicOperations.FixedTimeEquals(
                    SCrypt.Generate(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(parts[0]), 16384, 8, 1, 64),
                    Convert.FromHexString(parts[1]));
            return false;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException) { return false; }
    }
    public string Encrypt(string password)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var plaintext = Encoding.UTF8.GetBytes(password);
        var ciphertext = new byte[plaintext.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return $"{Convert.ToHexString(nonce)}:{Convert.ToHexString(ciphertext)}:{Convert.ToHexString(tag)}";
    }
    public string Decrypt(string secret)
    {
        var parts = secret.Split(':');
        if (parts.Length != 3) throw new CryptographicException("数据库密码格式无效");
        var ciphertext = Convert.FromHexString(parts[1]); var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromHexString(parts[0]), ciphertext, Convert.FromHexString(parts[2]), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
