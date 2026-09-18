using System.Collections.Concurrent;

namespace AtlasMail.Domain.Rules;

/// <summary>
/// Rate-limit de intentos de AUTH fallidos por IP (spec §49 "brute-force login / SMTP AUTH").
/// Estado concurrency-safe POR IP (un candado por IP — NO un lock global que serialice todas
/// las conexiones). Expira y ELIMINA las entradas antiguas/inactivas para impedir crecimiento
/// ilimitado. Ventana y límite configurables.
/// Semántica: permite hasta <see cref="MaxFailures"/> fallos; el siguiente intento se bloquea.
/// </summary>
public sealed class AuthRateLimiter
{
    private sealed class IpState
    {
        public readonly object Sync = new();
        public readonly Queue<DateTime> Timestamps = new();
    }

    private const int DefaultMaxFailures = 10;
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, IpState> _states = new(StringComparer.Ordinal);

    /// <summary>Nº máximo de fallos permitidos en la ventana (0 = sin límite).</summary>
    public int MaxFailures { get; }
    /// <summary>Ventana deslizante.</summary>
    public TimeSpan Window { get; }

    public AuthRateLimiter(int maxFailures, TimeSpan window)
    {
        MaxFailures = maxFailures <= 0 ? DefaultMaxFailures : maxFailures;
        Window = window <= TimeSpan.Zero ? DefaultWindow : window;
    }

    public AuthRateLimiter() : this(DefaultMaxFailures, DefaultWindow) { }

    /// <summary>¿Puede esta IP intentar autenticarse ahora? (no registra).</summary>
    public bool IsAllowed(string ip)
    {
        if (MaxFailures <= 0) return true;
        if (!_states.TryGetValue(ip, out var st)) return true;
        return Check(ip, st, register: false);
    }

    /// <summary>Registra un fallo para esta IP y devuelve si el intento se considera permitido
    /// (false = ya bloqueado, excedió el límite).</summary>
    public bool RecordFailure(string ip)
    {
        if (MaxFailures <= 0) return true;
        var st = _states.GetOrAdd(ip, _ => new IpState());
        return Check(ip, st, register: true);
    }

    private bool Check(string ip, IpState st, bool register)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - Window;
        lock (st.Sync)
        {
            // expirar entradas de la ventana
            while (st.Timestamps.Count > 0 && st.Timestamps.Peek() < cutoff)
                st.Timestamps.Dequeue();

            int current = st.Timestamps.Count;
            bool allowed = current < MaxFailures;

            if (register)
            {
                st.Timestamps.Enqueue(now);
                if (st.Timestamps.Count >= MaxFailures)
                {
                    // Ya alcanzó el límite: el siguiente IsAllowed devuelve false.
                    return true; // el fallo que dispara el límite aún se registró
                }
                // Aún bajo el límite; limpiar si quedó vacío (expiración → eliminar entrada)
                if (st.Timestamps.Count == 0) _states.TryRemove(ip, out _);
                return true;
            }

            // consulta (no registra)
            if (current == 0) _states.TryRemove(ip, out _); // entrada inactiva → eliminarla
            return allowed;
        }
    }

    /// <summary>Nº de fallos vigentes de una IP (observabilidad/testing).</summary>
    public int Count(string ip)
    {
        if (!_states.TryGetValue(ip, out var st)) return 0;
        var now = DateTime.UtcNow;
        var cutoff = now - Window;
        lock (st.Sync)
        {
            while (st.Timestamps.Count > 0 && st.Timestamps.Peek() < cutoff)
                st.Timestamps.Dequeue();
            return st.Timestamps.Count;
        }
    }

    /// <summary>Nº de IPs con estado vigente (para tests de no-crecimiento).</summary>
    public int IpCount => _states.Count;
}