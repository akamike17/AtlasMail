using System.Collections.Concurrent;

namespace AtlasMail.Domain.Rules;

/// <summary>
/// Rate-limit de intentos de AUTH por IP (spec §49 "brute-force login / SMTP AUTH").
/// Estado concurrency-safe POR IP (un candado por IP — NO un lock global que serialice todas
/// las conexiones). Expira y ELIMINA las entradas antiguas/inactivas para impedir crecimiento
/// ilimitado. Ventana y límite configurables.
///
/// Semántica del límite:
///  - <see cref="TryBegin(string)"/> RESERVA un slot de forma ATOMICA por IP (bajo el lock de la IP).
///    Si el IP ya agotó su presupuesto en la ventana, devuelve false y NO reserva. Reservar a medida
///    que el intento comienza es lo que hace el límite estrictamente atómico bajo concurrencia:
///    N conexiones simultáneas compiten por N slots y N − k de ellas quedan bloqueadas, en vez de
///    que todas pasen el chequeo y solo algunas registren el fallo después (la debilidad del patrón
///    IsAllowed-then-RecordFailure).
///  - El consumidor, cuando la credencial es VÁLIDA, llama <see cref="CommitSuccess(string)"/> para
///    liberar el slot reservado (no cuenta como fallo).
///  - Si el intento FALLA, el slot reservado ya cuenta: no se libera.
///  - <see cref="IsAllowed(string)"/> / <see cref="RecordFailure(string)"/> se conservan por
///    compatibilidad/observabilidad (misma semántica previa), pero el camino real de SMTP usa
///    <see cref="TryBegin"/> + <see cref="CommitSuccess"/>.
///
/// Limpieza global: un timer interno llama periódicamente <see cref="SweepExpired"/> para eliminar
/// las entradas de IPs que abandonaron el sistema (una IP sin actividad tras la ventana no queda
/// como entrada permanente en memoria).
/// </summary>
public sealed class AuthRateLimiter : IDisposable
{
    private sealed class IpState
    {
        public readonly object Sync = new();
        public readonly Queue<DateTime> Timestamps = new();
        public DateTime LastSeenUtc;
    }

    private const int DefaultMaxFailures = 10;
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);
    // El barrido global corre con frecuencia relativa a la ventana (p. ej. cada min para una ventana de 15).
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, IpState> _states = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer? _sweepTimer;

    /// <summary>Nº máximo de fallos permitidos en la ventana (0 = sin límite).</summary>
    public int MaxFailures { get; }
    /// <summary>Ventana deslizante.</summary>
    public TimeSpan Window { get; }

    public AuthRateLimiter(int maxFailures, TimeSpan window, TimeSpan? sweepInterval = null)
    {
        MaxFailures = maxFailures <= 0 ? DefaultMaxFailures : maxFailures;
        Window = window <= TimeSpan.Zero ? DefaultWindow : window;
        var interval = sweepInterval ?? DefaultSweepInterval;
        if (interval > TimeSpan.Zero)
        {
            // Timer de limpieza global (no-daemon thread-pool); se descarta con Dispose.
            _sweepTimer = new System.Threading.Timer(_ => { try { SweepExpired(); } catch { /* best effort */ } },
                null, interval, interval);
        }
    }

    public AuthRateLimiter() : this(DefaultMaxFailures, DefaultWindow) { }

    public void Dispose() => _sweepTimer?.Dispose();

    /// <summary>
    /// Reserva atomáticamente un slot de intento para esta IP (bajo el lock de la IP). Devuelve true
    /// si el intento puede proceder (el slot queda contado mientras dura), false si la IP ya superó su
    /// presupuesto en la ventana. Cuando la autenticación tiene ÉXITO el consumidor debe llamar
    /// <see cref="CommitSuccess"/> para no contar ese slot como fallo.
    /// </summary>
    public bool TryBegin(string ip)
    {
        if (MaxFailures <= 0) return true;
        var st = _states.GetOrAdd(ip, _ => new IpState());
        return Reserve(st);
    }

    /// <summary>Libera un slot reservado (autenticación exitosa): lo quita de la ventana para no contarlo
    /// como fallo. Si la entrada queda vacía y no ha habido actividad, se elimina.</summary>
    public void CommitSuccess(string ip)
    {
        if (MaxFailures <= 0) return;
        if (!_states.TryGetValue(ip, out var st)) return;
        lock (st.Sync)
        {
            st.LastSeenUtc = DateTime.UtcNow;
            if (st.Timestamps.Count > 0) st.Timestamps.Dequeue();
            if (st.Timestamps.Count == 0) _states.TryRemove(ip, out _);
        }
    }

    /// <summary>¿Puede esta IP intentar autenticarse ahora? (no registra; solo consulta).</summary>
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

    private bool Reserve(IpState st)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - Window;
        lock (st.Sync)
        {
            st.LastSeenUtc = now;
            while (st.Timestamps.Count > 0 && st.Timestamps.Peek() < cutoff)
                st.Timestamps.Dequeue();

            if (st.Timestamps.Count >= MaxFailures)
                return false; // sin presupuesto; no se reserva

            st.Timestamps.Enqueue(now);
            return true;
        }
    }

    private bool Check(string ip, IpState st, bool register)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - Window;
        lock (st.Sync)
        {
            st.LastSeenUtc = now;
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

    /// <summary>
    /// Elimina globalmente las entradas de IPs sin actividad durante al menos la ventana, de modo
    /// que IPs abandonadas no queden como entrada permanente en <see cref="_states"/>. Se invoca
    /// periódicamente por el timer interno y puede llamarse a mano (tests).
    /// </summary>
    public void SweepExpired()
    {
        var cutoff = DateTime.UtcNow - Window;
        foreach (var (ip, st) in _states)
        {
            lock (st.Sync)
            {
                // Expirar timestamps vencidos dentro de la ventana.
                while (st.Timestamps.Count > 0 && st.Timestamps.Peek() < cutoff)
                    st.Timestamps.Dequeue();
                // Sin actividad reciente y sin fallos vigentes → eliminar la entrada.
                if (st.Timestamps.Count == 0 && st.LastSeenUtc < cutoff)
                    _states.TryRemove(ip, out _);
            }
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