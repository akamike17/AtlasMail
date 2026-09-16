namespace AtlasMail.Domain.Rules;

/// <summary>Reglas de cuota de buzón (sección 39). Cálculo puro, auditable.</summary>
public static class QuotaPolicy
{
    /// <summary>¿Acepta un mensaje de talla size sin exceder la cuota?</summary>
    public static bool Accepts(long usedBytes, long quotaBytes, long sizeBytes)
    {
        if (quotaBytes <= 0) return true;           // sin límite (0 = ilimitado)
        if (sizeBytes < 0) return false;
        checked
        {
            try { return usedBytes + sizeBytes <= quotaBytes; }
            catch (OverflowException) { return false; }
        }
    }

    /// <summary>Cuota libre restante, o 0 si está al límite.</summary>
    public static long Remaining(long usedBytes, long quotaBytes)
    {
        if (quotaBytes <= 0) return long.MaxValue;
        var rem = quotaBytes - usedBytes;
        return rem < 0 ? 0 : rem;
    }
}

/// <summary>
/// Política de retries con backoff exponencial (sección 7). No loops infinitos.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Base de backoff en segundos para un intento (1-based).</summary>
    public static TimeSpan Backoff(int attemptNumber, TimeSpan baseDelay, int capSeconds = 3600)
    {
        if (attemptNumber <= 1) return baseDelay;
        double exp = baseDelay.TotalSeconds * Math.Pow(2, attemptNumber - 2);
        var seconds = Math.Min(exp, capSeconds);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    /// <summary>¿Debe re-intentarse con N intentos hechos? (sin loop infinito)</summary>
    public static bool ShouldRetry(int attempts, int maxAttempts) => attempts < maxAttempts;
}