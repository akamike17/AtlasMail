using AtlasMail.Domain.Mime;

namespace AtlasMail.Application.Abstractions;

/// <summary>
/// Almacenamiento de mensajes (sección 1). NO guardar MIME por varchar gigante en MySQL.
/// Implementaciones: FileSystemMessageStore (inicial), ObjectStorageMessageStore (futuro).
/// </summary>
public interface IMessageStore
{
    /// <summary>Persiste el MIME crudo y devuelve una clave de almacenamiento.</summary>
    Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default);
    /// <summary>Lee el MIME crudo por clave.</summary>
    Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default);
    /// <summary>Elimina un mensaje del almacén.</summary>
    Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default);
    /// <summary>Lista las claves almacenadas (para backup).</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default);
    /// <summary>Espacio total usado (bytes) en el almacén.</summary>
    Task<long> TotalSizeAsync(CancellationToken ct = default);
}

/// <summary>Motor de búsqueda de mensajes (sección 12). Aislamiento: solo mensajes autorizados.</summary>
public interface IMessageSearchService
{
    /// <summary>Devuelve los metadatos de los mensajes del buzón que cumplen el filtro.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(long mailboxId, MessageSearchQuery query, CancellationToken ct = default);
}

/// <summary>Escáner de adjuntos (sección 21). No-op explícito salvo integración real.</summary>
public interface IAttachmentScanner
{
    Task<Domain.Enums.AttachmentScanStatus> ScanAsync(Domain.Mime.AttachmentPart attachment, CancellationToken ct = default);
}

/// <summary>IA opcional y desacoplada (sección 31). Disabled por defecto; servidor funciona sin ella.</summary>
public interface IMailIntelligenceService
{
    bool Enabled { get; }
}

public sealed record MessageSearchQuery(
    string? Sender = null,
    string? Recipient = null,
    string? Subject = null,
    string? Body = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    string? AttachmentName = null,
    int Take = 100);

public sealed record SearchResult(
    long MessageId, string SenderAddress, string Subject, DateTime DateUtc,
    string BodyPreview, bool IsHtml, bool IsRead, bool IsFlagged, long SizeBytes);

/// <summary>
/// Resolución de MX para entrega externa (FASE 2, sección 8).
/// Devuelve los servidores de intercambio de correo de un dominio por prioridad.
/// Fallback: si el dominio no publica MX, se usa el registro A (RFC 5321 §5.1).
/// </summary>
public interface IMxResolver
{
    /// <summary>
    /// Resuelve los servidores SMTP de un dominio, ordenados por preferencia (menor = mayor prioridad).
    /// Puede devolver una lista vacía si no hay MX ni A resolubles.
    /// </summary>
    Task<IReadOnlyList<MailExchange>> ResolveAsync(string domainName, CancellationToken ct = default);
}

/// <summary>Un intercambio MX resuelto: host + preferencia (port 25 salvo override para tests).</summary>
public sealed record MailExchange(string Host, int Preference, int Port = 25);

/// <summary>
/// Aplica la política de envío autenticado / límites de entrega externa (FASE 2).
/// Encapsula la lectura de policy y la clasificación de errores 4xx/5xx para el worker.
/// </summary>
public interface IExternalDeliveryPolicy
{
    /// <summary>¿Puede este dominio local encaminar correo a destinos externos?</summary>
    Task<bool> AllowDomainExternalSendAsync(string domainName, CancellationToken ct = default);
}

/// <summary>Resultado de enviar un mensaje a un host SMTP concreto.</summary>
public sealed record SmtpSendResult(bool Success, string Response, bool Temporary)
{
    /// <summary>El fallo es permanente (5xx) → bounce/DSN.</summary>
    public bool Permanent => !Success && !Temporary;
}

/// <summary>Opciones de envío externo (TLS, credenciales).</summary>
public sealed record SmtpSendOptions(bool StartTlsRequired, string? Username, string? Password);

/// <summary>
/// Envío de un MIME a un host remoto por SMTP. Implementación en Protocols
/// (SmtpClient); abstraída aquí para que Application no dependa de Protocols.
/// </summary>
public interface IExternalMailSender
{
    Task<SmtpSendResult> SendAsync(string host, int port, string mailFrom, IReadOnlyList<string> rcptList,
        byte[] rawMime, string heloName, SmtpSendOptions options, CancellationToken ct = default);
}

/// <summary>Configuración global de entrega externa (FASE 2, sección 8).</summary>
public sealed record ExternalDeliverySettings(
    int MaxRetries = 6,
    TimeSpan ConnectTimeout = default,
    bool StartTlsRequiredForExternal = false,
    int RateLimitPerMinute = 0,          // 0 = sin límite
    int RateLimitPerDomainPerMinute = 0, // 0 = sin límite
    string? HeloName = null);

/// <summary>
/// Resultado de la entrega externa (por conectar al worker): decide si reintentar,
/// fallar permanente o reencolar. Applied por OutboundQueueService.
/// </summary>
public sealed record ExternalDeliveryOutcome(
    bool Delivered,
    string? RemoteResponse,
    ExternalOutcomeKind Kind);

public enum ExternalOutcomeKind
{
    Delivered,
    TemporaryFailure,   // reintentar (4xx / timeout / DNS unavailability)
    PermanentFailure,   // 5xx → bounce/DSN
    PolicyDenied,       // dominio no autorizado a enviar externo
    NoMxEntry
}