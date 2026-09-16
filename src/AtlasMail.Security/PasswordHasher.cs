using System.Security.Cryptography;
using System.Text;
using AtlasMail.Application;

namespace AtlasMail.Security;

/// <summary>
/// Hash de contraseñas PBKDF2 (RFC 2898) con sal aleatoria y formato versionado
/// (sección 4 del spec: nunca almacenar contraseñas reversibles).
/// Formato: 1$iterations$base64(salt)$base64(hash)
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int DefaultIterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations) => _iterations = iterations;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hashKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, KeySize);
        return $"1${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hashKey)}";
    }

    public bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash)) return false;
        var parts = storedHash.Split('$');
        if (parts.Length != 4) return false;
        if (parts[0] != "1") return false;
        if (!int.TryParse(parts[1], out int iterations) || iterations < 1) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Política de contraseñas (fuerza mínima configurable).</summary>
public sealed class DefaultPasswordPolicy : IPasswordPolicy
{
    public int MinLength { get; }
    public bool RequireDigit { get; }
    public bool RequireUppercase { get; }
    public bool RequireLowercase { get; }
    public bool RequireNonAlphanumeric { get; }

    public DefaultPasswordPolicy(int minLength = 8, bool requireDigit = true, bool requireUppercase = true,
        bool requireLowercase = true, bool requireNonAlphanumeric = true)
    {
        MinLength = minLength; RequireDigit = requireDigit; RequireUppercase = requireUppercase;
        RequireLowercase = requireLowercase; RequireNonAlphanumeric = requireNonAlphanumeric;
    }

    public bool IsCompliant(string password, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(password)) { reason = "vacía"; return false; }
        if (password.Length < MinLength) { reason = $"mínimo {MinLength} caracteres"; return false; }
        if (RequireDigit && !password.Any(char.IsDigit)) { reason = "requiere dígito"; return false; }
        if (RequireUppercase && !password.Any(char.IsUpper)) { reason = "requiere mayúscula"; return false; }
        if (RequireLowercase && !password.Any(char.IsLower)) { reason = "requiere minúscula"; return false; }
        if (RequireNonAlphanumeric && password.All(char.IsLetterOrDigit)) { reason = "requiere carácter especial"; return false; }
        return true;
    }
}