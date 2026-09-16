using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Rules;

namespace AtlasMail.UnitTests;

public class RateLimiterTests
{
    [Fact]
    public void Permite_hasta_el_limite_y_luego_bloquea()
    {
        var limiter = new SlidingWindowRateLimiter(60);
        for (int i = 0; i < 5; i++)
            Assert.True(limiter.Check("k", 5).Allowed);
        Assert.False(limiter.Check("k", 5).Allowed);
    }

    [Fact]
    public void Limite_cero_sin_bloqueo()
    {
        var limiter = new SlidingWindowRateLimiter(60);
        Assert.True(limiter.Check("k", 0).Allowed);
    }

    [Fact]
    public void Limites_independientes_por_clave()
    {
        var limiter = new SlidingWindowRateLimiter(60);
        Assert.True(limiter.Check("A", 2).Allowed);
        Assert.True(limiter.Check("B", 2).Allowed);
        Assert.True(limiter.Check("A", 2).Allowed);
        Assert.False(limiter.Check("A", 2).Allowed);
        Assert.True(limiter.Check("C", 5).Allowed);
    }
}

// Fakes for ExternalDeliveryService
internal sealed class FakeMxResolver : IMxResolver
{
    private readonly Dictionary<string, IReadOnlyList<MailExchange>> _map = new();
    public FakeMxResolver With(string domain, params MailExchange[] mxs)
    {
        _map[domain.ToLowerInvariant()] = mxs;
        return this;
    }
    public Task<IReadOnlyList<MailExchange>> ResolveAsync(string domainName, CancellationToken ct = default)
    {
        return Task.FromResult(_map.TryGetValue(domainName.ToLowerInvariant(), out var m) ? m : Array.Empty<MailExchange>());
    }
}

internal sealed class FakeMailSender : IExternalMailSender
{
    public Queue<(string Host, SmtpSendResult Result)> Queue = new();
    public FakeMailSender Success(string host) { Queue.Enqueue((host, new SmtpSendResult(true, "250 2.0.0 OK", false))); return this; }
    public FakeMailSender Permanent(string host, string resp = "550 5.1.1 User unknown") { Queue.Enqueue((host, new SmtpSendResult(false, resp, false))); return this; }
    public FakeMailSender Temporary(string host, string resp = "451 4.3.0 Try later") { Queue.Enqueue((host, new SmtpSendResult(false, resp, true))); return this; }

    public Task<SmtpSendResult> SendAsync(string host, int port, string mailFrom, IReadOnlyList<string> rcptList,
        byte[] rawMime, string heloName, SmtpSendOptions options, CancellationToken ct = default)
    {
        var pair = Queue.Count > 0 ? Queue.Dequeue() : ("default", new SmtpSendResult(false, "451 no route", true));
        return Task.FromResult(pair.Item2);
    }
}

internal sealed class AlwaysAllowPolicy : IExternalDeliveryPolicy
{
    public Task<bool> AllowDomainExternalSendAsync(string domainName, CancellationToken ct = default) => Task.FromResult(true);
}

internal sealed class DenyPolicy : IExternalDeliveryPolicy
{
    public Task<bool> AllowDomainExternalSendAsync(string domainName, CancellationToken ct = default) => Task.FromResult(false);
}

public class ExternalDeliveryServiceTests
{
    private static ExternalDeliveryService Build(IMxResolver mx, IExternalMailSender sender, IExternalDeliveryPolicy? policy = null)
        => new(mx, sender, policy ?? new AlwaysAllowPolicy(), new ExternalDeliverySettings(HeloName: "test.local"));

    private static byte[] Mime(string body = "hola") =>
        System.Text.Encoding.UTF8.GetBytes($"Subject: x\r\n\r\n{body}\r\n");

    [Fact]
    public async Task Entrega_exitosa_via_mx()
    {
        var mx = new FakeMxResolver().With("gmail.com", new MailExchange("mx1.gmail.com", 5));
        var sender = new FakeMailSender().Success("mx1.gmail.com");
        var svc = Build(mx, sender);

        var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@gmail.com", Mime());

        Assert.True(outcome.Delivered);
        Assert.Equal(ExternalOutcomeKind.Delivered, outcome.Kind);
    }

    [Fact]
    public async Task Fallo_permanente_5xx_marca_5xx()
    {
        var mx = new FakeMxResolver().With("hotmail.com", new MailExchange("mx1.hotmail.com", 10));
        var sender = new FakeMailSender().Permanent("mx1.hotmail.com");
        var svc = Build(mx, sender);

        var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@hotmail.com", Mime());

        Assert.False(outcome.Delivered);
        Assert.Equal(ExternalOutcomeKind.PermanentFailure, outcome.Kind);
    }

    [Fact]
    public async Task Fallo_temporal_reintenta()
    {
        var mx = new FakeMxResolver().With("temp.example", new MailExchange("mx.temp.example", 1));
        var sender = new FakeMailSender().Temporary("mx.temp.example");
        var svc = Build(mx, sender);

        var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@temp.example", Mime());

        Assert.False(outcome.Delivered);
        Assert.Equal(ExternalOutcomeKind.TemporaryFailure, outcome.Kind);
    }

    [Fact]
    public async Task Sin_MX_devuelve_NoMxEntry()
    {
        var mx = new FakeMxResolver(); // sin dominios
        var sender = new FakeMailSender();
        var svc = Build(mx, sender);

        var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@nodomain.example", Mime());

        Assert.False(outcome.Delivered);
        Assert.Equal(ExternalOutcomeKind.NoMxEntry, outcome.Kind);
    }

    [Fact]
    public async Task Politica_denegada_bloquea_envio()
    {
        var mx = new FakeMxResolver().With("ext.io", new MailExchange("mx.ext.io", 1));
        var sender = new FakeMailSender();
        var svc = Build(mx, sender, new DenyPolicy());

        var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@ext.io", Mime());

        Assert.False(outcome.Delivered);
        Assert.Equal(ExternalOutcomeKind.PolicyDenied, outcome.Kind);
    }

    [Fact]
    public async Task Intenta_SIGUIENTE_mx_en_fallo_temporal()
    {
        var mx = new FakeMxResolver().With("multi.example",
            new MailExchange("mx1.multi.example", 10), new MailExchange("mx2.multi.example", 20));
        var sender = new FakeMailSender().Temporary("mx1.multi.example").Success("mx2.multi.example");
        var svc = Build(mx, sender);

        var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@multi.example", Mime());

        Assert.True(outcome.Delivered);
    }
}