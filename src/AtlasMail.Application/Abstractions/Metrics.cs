using System.Collections.Concurrent;
using System.Globalization;

namespace AtlasMail.Application.Abstractions;

/// <summary>
/// Registro de métricas de observabilidad (spec §32). Deliberadamente NO expone datos
/// sensibles (direcciones, asuntos, contenido): sólo contadores y totales agregados.
/// Nombres estables para que el pipeline/panel los consuma.
/// </summary>
public interface IMetricsRegistry
{
    void Increment(string name, double amount = 1);
    void SetGauge(string name, double value);
    void Observe(string name, TimeSpan elapsed);
    IReadOnlyDictionary<string, double> Snapshot();
}

/// <summary>Métricas centralizadas thread-safe (singleton).</summary>
public sealed class MetricsRegistry : IMetricsRegistry
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    // gauge: valores puntuales (job depth, storage, etc.)
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    // histogramas simples: sum + count por timer
    private readonly ConcurrentDictionary<string, (double Sum, long Count)> _timers = new();

    public void Increment(string name, double amount = 1)
    {
        _counters.AddOrUpdate(name, (long)amount, (_, old) => (long)(old + amount));
    }

    public void SetGauge(string name, double value)
    {
        _gauges[name] = value;
    }

    public void Observe(string name, TimeSpan elapsed)
    {
        var ms = elapsed.TotalMilliseconds;
        _timers.AddOrUpdate(name, (ms, 1), (_, old) => (old.Sum + ms, old.Count + 1));
    }

    public IReadOnlyDictionary<string, double> Snapshot()
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, v) in _counters) d[k] = v;
        foreach (var (k, v) in _gauges) d[k] = v;
        foreach (var (k, (sum, count)) in _timers)
        {
            d[k + "_count"] = count;
            if (count > 0) d[k + "_sum_ms"] = sum;
        }
        return d;
    }
}

/// <summary>Snapshot a texto plano estilo Prometheus (nombres sanitizados).</summary>
public static class MetricsFormat
{
    public static string ToText(IReadOnlyDictionary<string, double> metrics)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (k, v) in metrics.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var name = Sanitize(k);
            sb.Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        // sólo letras, números, guiones y guiones bajos
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }
}