namespace AtlasMail.Application;

/// <summary>
/// Hash seguro de contraseñas (sección 4: nunca almacenar reversibles).
/// Implementación PBKDF2 en AtlasMail.Security.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string storedHash);
}

/// <summary>Validación de política de contraseñas (fuerza mínima configurable).</summary>
public interface IPasswordPolicy
{
    bool IsCompliant(string password, out string? reason);
    int MinLength { get; }
    bool RequireDigit { get; }
    bool RequireUppercase { get; }
    bool RequireLowercase { get; }
    bool RequireNonAlphanumeric { get; }
}