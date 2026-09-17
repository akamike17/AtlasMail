using AtlasMail.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Application.Services;

/// <summary>
/// Fachada de IA de un buzón (FASE 8, spec §31): siempre usa la IA local heurística; usa el backend
/// remoto SOLO si el backend está habilitado por configuración explícita Y el buzón dio consentimiento
/// (`Mailbox.AiConsent`). Garantiza el doble consentimiento antes de enviar contenido a un proveedor.
/// </summary>
public sealed class MailIntelligenceFacade
{
    private readonly IApplicationDbContext _db;
    private readonly IMailIntelligenceService _local;
    private readonly IMailIntelligenceBackend? _backend;
    private readonly ILogger<MailIntelligenceFacade> _logger;

    public MailIntelligenceFacade(IApplicationDbContext db, IMailIntelligenceService local,
        IMailIntelligenceBackend? backend, ILogger<MailIntelligenceFacade> logger)
    {
        _db = db; _local = local; _backend = backend; _logger = logger;
    }

    public bool BackendEnabled => _backend?.Enabled == true;

    /// <summary>¿El buzón da consentimiento para usar el backend remoto?</summary>
    public async Task<bool> HasConsentAsync(long mailboxId, CancellationToken ct = default)
    {
        var mailbox = await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailboxId, ct);
        return mailbox?.AiConsent == true;
    }

    /// <summary>Establece/revoca el consentimiento del buzón para usar el backend remoto.</summary>
    public async Task SetConsentAsync(long mailboxId, bool grant, CancellationToken ct = default)
    {
        var mailbox = await _db.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct)
            ?? throw new InvalidOperationException("Buzón no existe");
        if (mailbox.AiConsent != grant)
        {
            mailbox.AiConsent = grant;
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Análisis local siempre disponible (prioridad/clasificación/resumen/phishing).</summary>
    public MailIntelligenceResult Analyze(SubjectBody input)
    {
        var ph = _local.AssessPhishing(input);
        return new MailIntelligenceResult(
            _local.Priority(input), _local.Classify(input).ToString(),
            _local.Summarize(input), ph.Score, ph.Signals);
    }

    /// <summary>Resumen: usa backend si hay consentimiento y está habilitado; si no, local.</summary>
    public async Task<string> SummarizeAsync(long mailboxId, SubjectBody input, CancellationToken ct = default)
    {
        if (_backend is { Enabled: true } && await HasConsentAsync(mailboxId, ct))
            return await GuardAsync(() => _backend.SummarizeAsync(input, 200, ct), _local.Summarize(input));
        return _local.Summarize(input);
    }

    public async Task<string> TranslateAsync(long mailboxId, SubjectBody input, string toLang, CancellationToken ct = default)
    {
        if (_backend is { Enabled: true } && await HasConsentAsync(mailboxId, ct))
            return await GuardAsync(() => _backend.TranslateAsync(input, toLang, ct), input.Body);
        return input.Body; // sin backend no se traduce
    }

    public async Task<string> SuggestReplyAsync(long mailboxId, SubjectBody input, CancellationToken ct = default)
    {
        if (_backend is { Enabled: true } && await HasConsentAsync(mailboxId, ct))
            return await GuardAsync(() => _backend.SuggestReplyAsync(input, ct), "[backend deshabilitado]");
        return "[sugerencia de respuesta no disponible: habilita IA avanzada en configuración y da consentimiento en tu buzón]";
    }

    public async Task<string> DraftAsync(long mailboxId, SubjectBody input, string? tone, CancellationToken ct = default)
    {
        if (_backend is { Enabled: true } && await HasConsentAsync(mailboxId, ct))
            return await GuardAsync(() => _backend.DraftAsync(input, tone, ct), input.Subject + "\n\n" + input.Body);
        return input.Subject + "\n\n" + input.Body;
    }

    public async Task<string> ClassifyAsync(long mailboxId, SubjectBody input, CancellationToken ct = default)
    {
        if (_backend is { Enabled: true } && await HasConsentAsync(mailboxId, ct))
            return await GuardAsync(() => _backend.ClassifyAsync(input, ct), _local.Classify(input).ToString());
        return _local.Classify(input).ToString();
    }

    public async Task<SemanticSearchResult> SemanticSearchAsync(long mailboxId, string query, IReadOnlyList<string> docs, int topK = 5, CancellationToken ct = default)
    {
        if (_backend is { Enabled: true } && await HasConsentAsync(mailboxId, ct))
            return await GuardAsync(() => _backend.SemanticSearchAsync(query, docs, topK, ct),
                new SemanticSearchResult(Array.Empty<string>(), "fallback"));
        // fallback local: similaridad coseno simple sobre el texto (token overlap)
        var scored = docs
            .Select((d, i) => (i, score: OverlapScore(query.ToLowerInvariant(), d.ToLowerInvariant())))
            .OrderByDescending(x => x.score).ThenBy(x => x.i).Take(topK)
            .Select(x => x.i.ToString()).ToList();
        return new SemanticSearchResult(scored, "local");
    }

    private static double OverlapScore(string a, string b)
    {
        var ta = a.Split(new[] { ' ', ',', '.', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var tb = b.Split(new[] { ' ', ',', '.', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (ta.Length == 0) return 0;
        return ta.Average(t => tb.Any(x => x.Contains(t) || t.Contains(x)) ? 1 : 0);
    }

    private async Task<T> GuardAsync<T>(Func<Task<T>> fn, T fallback)
    {
        try { return await fn(); }
        catch (Exception ex) { _logger.LogWarning("Backend IA falló; fallback: {Msg}", ex.Message); return fallback; }
    }
}