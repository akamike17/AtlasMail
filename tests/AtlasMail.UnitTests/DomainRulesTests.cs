using AtlasMail.Domain.ValueObjects;
using AtlasMail.Domain.Rules;

namespace AtlasMail.UnitTests;

public class EmailAddressTests
{
    [Theory]
    [InlineData("alice@atlas.local", "alice", "atlas.local")]
    [InlineData("a.b+c@example.com", "a.b+c", "example.com")]
    [InlineData("user+tag@midominio.net", "user+tag", "midominio.net")]
    [InlineData("A@B.CD", "A", "B.CD")]
    public void Parse_valido(string raw, string local, string domain)
    {
        var ok = EmailAddress.TryParse(raw, out var addr);
        Assert.True(ok);
        Assert.Equal(local, addr.LocalPart);
        Assert.Equal(domain, addr.Domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sin arroba")]
    [InlineData("a@")]
    [InlineData("@dominio")]
    [InlineData("a@@b.com")]
    [InlineData("a b@c.com")]
    [InlineData("a@b_c.com")]
    [InlineData("a@-mall.com")]
    [InlineData("a@.com")]
    public void Parse_invalido(string raw)
    {
        Assert.False(EmailAddress.TryParse(raw, out _));
    }

    [Fact]
    public void PlusAddressing_se_extrae_correctamente()
    {
        var addr = EmailAddress.Parse("user+tag@x.com");
        Assert.True(addr.HasPlusTag);
        Assert.Equal("tag", addr.Tag);
        Assert.Equal("user@x.com", addr.WithoutPlusTag().Full);
    }
}

public class RelayPolicyTests
{
    [Fact]
    public void Anonimo_a_local_existente_entrega_local()
    {
        var r = RelayPolicy.Evaluate(EmailAddress.Parse("ext@elsewhere.com"),
            EmailAddress.Parse("bob@atlas.local"), false, true, true, true);
        Assert.Equal(RelayDecision.DeliverLocal, r.Decision);
    }

    [Fact]
    public void Anonimo_a_local_inexistente_denegado()
    {
        var r = RelayPolicy.Evaluate(EmailAddress.Parse("ext@elsewhere.com"),
            EmailAddress.Parse("ghost@atlas.local"), false, true, false, true);
        Assert.Equal(RelayDecision.Deny, r.Decision);
    }

    [Fact]
    public void Anonimo_a_externo_NUNCA_relay()
    {
        var r = RelayPolicy.Evaluate(EmailAddress.Parse("ext@elsewhere.com"),
            EmailAddress.Parse("v@gmail.com"), false, false, false, true);
        Assert.Equal(RelayDecision.Deny, r.Decision);
    }

    [Fact]
    public void Autenticado_a_externo_permitido_si_politica()
    {
        var r = RelayPolicy.Evaluate(EmailAddress.Parse("alice@atlas.local"),
            EmailAddress.Parse("v@gmail.com"), true, false, false, true);
        Assert.Equal(RelayDecision.DeliverRemote, r.Decision);
    }

    [Fact]
    public void Autenticado_a_externo_denegado_si_politica_lo_prohibe()
    {
        var r = RelayPolicy.Evaluate(EmailAddress.Parse("alice@atlas.local"),
            EmailAddress.Parse("v@gmail.com"), true, false, false, false);
        Assert.Equal(RelayDecision.Deny, r.Decision);
    }
}

public class QuotaPolicyTests
{
    [Fact]
    public void Acepta_con_espacio_suficiente()
    {
        Assert.True(QuotaPolicy.Accepts(100, 1000, 900));
    }

    [Fact]
    public void Rechaza_al_exceder_quota()
    {
        Assert.False(QuotaPolicy.Accepts(100, 1000, 901));
    }

    [Fact]
    public void Sin_limite_si_quota_cero()
    {
        Assert.True(QuotaPolicy.Accepts(long.MaxValue, 0, long.MaxValue));
    }

    [Fact]
    public void Rechaza_overflow()
    {
        Assert.False(QuotaPolicy.Accepts(long.MaxValue - 1, long.MaxValue, 10));
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void Backoff_crece_exponencial_pero_con_topping()
    {
        var b1 = RetryPolicy.Backoff(1, TimeSpan.FromSeconds(60));
        var b3 = RetryPolicy.Backoff(3, TimeSpan.FromSeconds(60));
        Assert.True(b1 < b3);
        Assert.True(b3 <= TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Deja_de_reintentar_al_llegar_al_maximo()
    {
        Assert.False(RetryPolicy.ShouldRetry(6, 6));
        Assert.True(RetryPolicy.ShouldRetry(5, 6));
    }
}

public class PasswordHasherTests
{
    private readonly Security.Pbkdf2PasswordHasher _hasher = new(10_000);

    [Fact]
    public void Hash_y_verify_roundtrip()
    {
        string hash = _hasher.Hash("Atl4smail1!");
        Assert.True(_hasher.Verify("Atl4smail1!", hash));
        Assert.False(_hasher.Verify("otraPass1!", hash));
    }

    [Fact]
    public void Hashes_son_distintos_con_distinta_sal()
    {
        Assert.NotEqual(_hasher.Hash("Igual1!"), _hasher.Hash("Igual1!"));
    }

    [Fact]
    public void Formato_contiene_parts()
    {
        var hash = _hasher.Hash("Abcdef1!");
        var parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("1", parts[0]);
    }
}

public class DefaultPasswordPolicyTests
{
    private readonly Security.DefaultPasswordPolicy _policy = new();

    [Theory]
    [InlineData("Atl4smail1!", true)]
    [InlineData("holamundo", false)]
    [InlineData("SoloLetras1", false)]
    [InlineData("cortas1!", false)]
    public void Cumple_politica(string pwd, bool expected)
    {
        Assert.Equal(expected, _policy.IsCompliant(pwd, out _));
    }
}