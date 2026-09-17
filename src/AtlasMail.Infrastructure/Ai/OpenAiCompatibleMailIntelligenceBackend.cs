using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtlasMail.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Infrastructure.Ai;

/// <summary>
/// Backend de IA vía HTTP a un endpoint OpenAI-compatible (FASE 8, spec §31). No se activa por defecto:
/// requiere `Ai:Backend:Enabled=true`, `Ai:Backend:Endpoint`, `Ai:Backend:ApiKey` y (opcional) `Model`.
/// Si el endpoint no responde, los métodos devuelven fallback local (nunca rompen el flujo) y se registra
/// un aviso. El consentimiento por buzón (`Mailbox.AiConsent`) se evalúa en la capa de servicio, no aquí.
/// </summary>
public sealed class OpenAiCompatibleMailIntelligenceBackend : IMailIntelligenceBackend
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly bool _enabled;

    public OpenAiCompatibleMailIntelligenceBackend(HttpClient http, string endpoint, string? apiKey,
        string model, ILogger logger, bool enabled)
    {
        _http = http; _endpoint = endpoint.TrimEnd('/'); _apiKey = apiKey; _model = model; _logger = logger; _enabled = enabled;
    }

    public bool Enabled => _enabled;
    public string Provider => "openai-compatible:" + _model;

    public async Task<string> SummarizeAsync(SubjectBody input, int maxChars, CancellationToken ct = default)
    {
        if (!_enabled) return FallbackSummarize(input, maxChars);
        return await CompleteAsync(
            $"Resume el siguiente correo en máximo {maxChars} caracteres. Asunto: {input.Subject}\nCuerpo:\n{input.Body}",
            temperature: 0.2, ct);
    }

    public async Task<string> TranslateAsync(SubjectBody input, string toLang, CancellationToken ct = default)
    {
        if (!_enabled) return input.Body; // sin backend no se traduce
        return await CompleteAsync($"Traduce el siguiente correo al idioma '{toLang}'. Mantén el tono.\nAsunto: {input.Subject}\nCuerpo:\n{input.Body}", temperature: 0.2, ct);
    }

    public async Task<string> SuggestReplyAsync(SubjectBody input, CancellationToken ct = default)
    {
        if (!_enabled) return $"[Sugerencia de respuesta no disponible sin backend. Asunto: {input.Subject}]";
        return await CompleteAsync(
            $"Escribe una respuesta breve y profesional al siguiente correo.\nAsunto: {input.Subject}\nCuerpo:\n{input.Body}", temperature: 0.6, ct);
    }

    public async Task<string> DraftAsync(SubjectBody input, string? tone, CancellationToken ct = default)
    {
        if (!_enabled) return input.Subject + "\n\n" + input.Body;
        var t = string.IsNullOrWhiteSpace(tone) ? "profesional" : tone;
        return await CompleteAsync($"Reescribe el siguiente borrador de correo con tono '{t}'. Preserva el contenido.\nAsunto: {input.Subject}\nCuerpo:\n{input.Body}", temperature: 0.6, ct);
    }

    public async Task<string> ClassifyAsync(SubjectBody input, CancellationToken ct = default)
    {
        if (!_enabled) return "General";
        var resp = await CompleteAsync(
            $"Clasifica este correo en exactamente UNA de estas categorías: General, Urgent, Finance, Marketing, Newsletter, Social, Notification, Security. Devuelve solo la categoría.\nAsunto: {input.Subject}\nCuerpo:\n{input.Body}",
            temperature: 0, ct);
        var c = resp.Trim();
        return Regex.IsMatch(c, @"(?i)urgent") ? "Urgent" : c;
    }

    public async Task<SemanticSearchResult> SemanticSearchAsync(string query, IReadOnlyList<string> docs, int topK = 5, CancellationToken ct = default)
    {
        if (!_enabled || docs.Count == 0) return LocalCosSim(query, docs, topK);
        // LLM: pedir índices. Fallback local si falla o no parsea.
        var block = string.Join("\n---\n", docs.Select((d, i) => $"[{i}] {d}"));
        var resp = await CompleteAsyncSafe(
            $"Dado la consulta: \"{query}\"\nDocumentos:\n{block}\nDevuelve solo la lista de índices (números) de los {topK} más relevantes, separados por coma.", temperature: 0, ct);
        var indices = Regex.Matches(resp ?? "", @"\b(\d+)\b")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(v => int.TryParse(v, out var i) && i < docs.Count)
            .Take(topK)
            .ToList();
        if (indices.Count == 0) return LocalCosSim(query, docs, topK);
        return new SemanticSearchResult(indices, "backend");
    }

    private static SemanticSearchResult LocalCosSim(string query, IReadOnlyList<string> docs, int topK)
    {
        var qToks = Tokenize(query);
        var scored = docs.Select((d, i) => (idx: i.ToString(), score: Cos(d, qToks))).OrderByDescending(x => x.score).Take(topK).ToList();
        return new SemanticSearchResult(scored.Select(x => x.idx).ToList(), "local-cosine");
    }

    private static string[] Tokenize(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-záéíóúñ0-9]+").Select(m => m.Value).ToArray();

    private static double Cos(string doc, string[] qToks)
    {
        var d = Tokenize(doc);
        if (d.Length == 0 || qToks.Length == 0) return 0;
        var df = d.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var qf = qToks.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        double dot = 0, na = 0, nb = 0;
        foreach (var (k, v) in qf)
        {
            dot += v * df.GetValueOrDefault(k);
            na += (double)v * v;
        }
        foreach (var v in df.Values) { var d1 = (double)v; nb += d1 * d1; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private async Task<string> CompleteAsync(string prompt, double temperature, CancellationToken ct)
    {
        var (ok, txt) = await TryCompleteAsync(prompt, temperature, ct);
        if (ok && !string.IsNullOrWhiteSpace(txt)) return txt.Trim();
        _logger.LogWarning("Backend IA sin respuesta; usando fallback local.");
        return FallbackFor(prompt);
    }

    private async Task<string?> CompleteAsyncSafe(string prompt, double temperature, CancellationToken ct)
    {
        var (ok, txt) = await TryCompleteAsync(prompt, temperature, ct);
        return ok ? txt : null;
    }

    private async Task<(bool ok, string? text)> TryCompleteAsync(string prompt, double temperature, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model = _model,
                temperature,
                max_tokens = 600,
                messages = new[] { new { role = "user", content = prompt } }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/v1/chat/completions");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) { _logger.LogWarning("Backend IA HTTP {Code}", (int)resp.StatusCode); return (false, null); }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                return (true, content.GetString());
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Backend IA error: {Msg}", ex.Message);
            return (false, null);
        }
    }

    private static string FallbackFor(string prompt)
    {
        // nunca dejar vacío: si tenía cuerpo, devolver el cuerpo truncado
        int split = prompt.IndexOf("Cuerpo:", StringComparison.Ordinal);
        var body = split >= 0 ? prompt[(split + 7)..] : "";
        var t = body.Trim();
        return t.Length > 220 ? t[..220] + "…" : t;
    }

    private static string FallbackSummarize(SubjectBody input, int maxChars)
    {
        var b = (input.Body ?? "").Trim();
        return b.Length > maxChars ? b[..maxChars].TrimEnd() + "…" : (b.Length == 0 ? input.Subject : b);
    }
}

/// <summary>Backend de IA deshabilitado (default): servidor funciona sin IA remota (spec §31).</summary>
public sealed class DisabledMailIntelligenceBackend : IMailIntelligenceBackend
{
    public bool Enabled => false;
    public string Provider => "none";
    public Task<string> SummarizeAsync(SubjectBody input, int maxChars, CancellationToken ct = default) => Task.FromResult("");
    public Task<string> TranslateAsync(SubjectBody input, string toLang, CancellationToken ct = default) => Task.FromResult(input.Body);
    public Task<string> SuggestReplyAsync(SubjectBody input, CancellationToken ct = default) => Task.FromResult("");
    public Task<string> DraftAsync(SubjectBody input, string? tone, CancellationToken ct = default) => Task.FromResult(input.Subject + "\n\n" + input.Body);
    public Task<string> ClassifyAsync(SubjectBody input, CancellationToken ct = default) => Task.FromResult("General");
    public Task<SemanticSearchResult> SemanticSearchAsync(string query, IReadOnlyList<string> docs, int topK = 5, CancellationToken ct = default)
        => Task.FromResult(new SemanticSearchResult(Array.Empty<string>(), "disabled"));
}