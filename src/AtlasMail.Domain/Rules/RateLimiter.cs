using System.Collections.Concurrent;

namespace AtlasMail.Domain.Rules;

/// <summary>
/// Resultado de una comprobación de límite de tasa.
/// </summary>
public readonly struct RateLimitDecision
{
    public bool Allowed { get; }
    public int Current { get; }
    public int Limit { get; }

    public RateLimitDecision(bool allowed, int current, int limit)
    {
        Allowed = allowed; Current = current; Limit = limit;
    }
}

/// <summary>
/// Rate limiting en memoria con ventana deslizante (sección 5 y FASE 2).
/// Aplica límites por IP / usuario / dominio de destino. La lógica es pura y
/// testeable fuera de contexto; la persistencia del counter es en memoria
/// (proceso actual) — operada por el worker de entrega.
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private sealed class Bucket
    {
        public readonly Queue<DateTime> Timestamps = new();
        public DateTime WindowStart = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly object _purgeLock = new();
    public int WindowSeconds { get; }

    public SlidingWindowRateLimiter(int windowSeconds)
    {
        WindowSeconds = windowSeconds <= 0 ? 60 : windowSeconds;
    }

    /// <summary>
    /// Comprueba y, si procede, registra una ocurrencia en la ventana.
    /// Retorna Allowed=false cuando current supera limit.
    /// </summary>
    public RateLimitDecision Check(string key, int limit)
    {
        if (limit <= 0) return new RateLimitDecision(true, 0, 0); // sin límite
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-WindowSeconds);

        // Limpiar entradas viejas de este bucket
        lock (bucket)
        {
            while (bucket.Timestamps.Count > 0 && bucket.Timestamps.Peek() < cutoff)
                bucket.Timestamps.Dequeue();

            int current = bucket.Timestamps.Count;
            if (current >= limit) return new RateLimitDecision(false, current, limit);

            bucket.Timestamps.Enqueue(now);
            return new RateLimitDecision(true, current + 1, limit);
        }
    }

    /// <summary>
    /// Estado actual sin registrar una ocurrencia (para observabilidad).
    /// </summary>
    public int Count(string key)
    {
        if (!_buckets.TryGetValue(key, out var bucket)) return 0;
        lock (bucket)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-WindowSeconds);
            while (bucket.Timestamps.Count > 0 && bucket.Timestamps.Peek() < cutoff)
                bucket.Timestamps.Dequeue();
            return bucket.Timestamps.Count;
        }
    }

    /// <summary>Libera memoria de claves inactivas. Debe llamarse bajo control (no en caliente excesivo).</summary>
    public void Purge()
    {
        lock (_purgeLock)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _buckets)
            {
                if (now - kv.Value.WindowStart > TimeSpan.FromSeconds(WindowSeconds) && Count(kv.Key) == 0)
                    _buckets.TryRemove(kv.Key, out _);
            }
        }
    }
}