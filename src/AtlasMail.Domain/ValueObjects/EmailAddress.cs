using System.Text.RegularExpressions;

namespace AtlasMail.Domain.ValueObjects;

/// <summary>
/// Parsing de direcciones de correo (forma localpart@dominio).
/// Soporta plus addressing (usuario+etiqueta@dominio) conforme a sección 41 del spec.
/// </summary>
public readonly struct EmailAddress : IEquatable<EmailAddress>
{
    public string LocalPart { get; }
    public string Domain { get; }

    private EmailAddress(string localPart, string domain)
    {
        LocalPart = localPart;
        Domain = domain;
    }

    private static readonly Regex LocalPattern =
        new(@"^[a-z0-9!#$%&'*+/=?^_`{|}~.-]{1,64}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DomainPattern =
        new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(string raw, out EmailAddress result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        int at = raw.IndexOf('@');
        if (at <= 0 || at != raw.LastIndexOf('@')) return false;

        string local = raw[..at];
        string domain = raw[(at + 1)..];
        if (!LocalPattern.IsMatch(local)) return false;
        if (!DomainPattern.IsMatch(domain)) return false;

        result = new EmailAddress(local, domain);
        return true;
    }

    public static EmailAddress Parse(string raw)
    {
        if (!TryParse(raw, out var r)) throw new ArgumentException($"Dirección inválida: {raw}");
        return r;
    }

    /// <summary>Dirección completa localpart@dominio.</summary>
    public string Full => $"{LocalPart}@{Domain}";

    /// <summary>Base sin etiqueta de plus addressing, p.ej. usuario+tag@x -> usuario@x.</summary>
    public EmailAddress WithoutPlusTag()
    {
        int plus = LocalPart.IndexOf('+');
        if (plus <= 0) return this;
        return new EmailAddress(LocalPart[..plus], Domain);
    }

    public bool HasPlusTag => LocalPart.Contains('+');

    public string Tag
    {
        get
        {
            int plus = LocalPart.IndexOf('+');
            return plus >= 0 ? LocalPart[(plus + 1)..] : string.Empty;
        }
    }

    public string DomainWithoutTld
    {
        get
        {
            int dot = Domain.IndexOf('.');
            return dot > 0 ? Domain[..dot] : Domain;
        }
    }

    public bool Equals(EmailAddress other) =>
        string.Equals(LocalPart, other.LocalPart, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Domain, other.Domain, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is EmailAddress e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(
        Normalize(LocalPart), Normalize(Domain));
    private static string Normalize(string s) => s.ToLowerInvariant();
    public override string ToString() => Full;

    public static bool operator ==(EmailAddress a, EmailAddress b) => a.Equals(b);
    public static bool operator !=(EmailAddress a, EmailAddress b) => !a.Equals(b);
}