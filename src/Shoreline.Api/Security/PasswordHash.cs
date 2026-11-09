using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;

namespace Shoreline.Api.Security;

public static class PasswordHash
{
    public static string Create(string password)
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        const int iterations = 1_000_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes(salt), iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:sha256:{iterations}${salt}${Convert.ToHexStringLower(hash)}";
    }

    // Werkzeug stores the algorithm, parameters, literal salt and hexadecimal hash.
    public static bool Verify(string encoded, string password)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 3) return false;
            var algorithm = parts[0].Split(':');
            var salt = Encoding.UTF8.GetBytes(parts[1]);
            var expected = Convert.FromHexString(parts[2]);
            byte[] actual;
            if (algorithm is ["pbkdf2", var digest, var count])
            {
                var iterations = int.Parse(count, CultureInfo.InvariantCulture);
                if (iterations is < 1 or > 10_000_000 || expected.Length is < 1 or > 128) return false;
                var hash = digest switch
                {
                    "sha1" => HashAlgorithmName.SHA1,
                    "sha256" => HashAlgorithmName.SHA256,
                    "sha512" => HashAlgorithmName.SHA512,
                    _ => throw new FormatException("Unsupported password digest")
                };
                actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hash, expected.Length);
            }
            else if (algorithm is ["scrypt", var n, var r, var p])
            {
                var cost = int.Parse(n, CultureInfo.InvariantCulture);
                var block = int.Parse(r, CultureInfo.InvariantCulture);
                var parallel = int.Parse(p, CultureInfo.InvariantCulture);
                if (cost is < 2 or > 131072 || (cost & (cost - 1)) != 0 || block is < 1 or > 16 || parallel is < 1 or > 4
                    || 128L * cost * block > 128L * 1024 * 1024 || expected.Length != 64)
                    return false;
                actual = SCrypt.Generate(Encoding.UTF8.GetBytes(password), salt, cost, block, parallel, 64);
            }
            else return false;
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    public static string Characteristic(string hash)
    {
        var tail = new string(hash.TakeLast(6).Reverse().ToArray());
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(tail)))[2..18];
    }
}
