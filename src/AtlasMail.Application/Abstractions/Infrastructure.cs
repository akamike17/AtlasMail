using AtlasMail.Domain.Enums;
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

/// <summary>IA opcional y desacoplada (sección 31). Disabled por defecto; servidor funciona sin ella.
/// La implementación es LOCAL/heurística y NUNCA envía el contenido del correo a proveedores externos
/// sin configuración/consentimiento explícito (§31). Asistida, nunca decisión final de seguridad.</summary>
public interface IMailIntelligenceService
{
    bool Enabled { get; }
    /// <summary>Prioridad (0-100) por señales locales.</summary>
    int Priority(SubjectBody subjectBody);
    /// <summary>Clasificación de negocio.</summary>
    MailIntelligenceCategory Classify(SubjectBody subjectBody);
    /// <summary>Riesgo de phishing asistido (0-100) + señales. No reemplaza a antimalware/antispam.</summary>
    PhishingAssessment AssessPhishing(SubjectBody subjectBody);
    /// <summary>Resumen extrativo local (máx caracteres).</summary>
    string Summarize(SubjectBody subjectBody, int maxChars = 180);
}

public sealed record SubjectBody(string Subject, string Body);
public sealed record PhishingAssessment(int Score, IReadOnlyList<string> Signals);

public enum MailIntelligenceCategory
{
    General = 0,
    Urgent = 1,
    Notification = 2,
    Finance = 3,
    Marketing = 4,
    Social = 5,
    Newsletter = 6,
    Security = 7
}

public sealed record MailIntelligenceResult(
    int Priority, string Category, string Summary, int PhishingScore, IReadOnlyList<string> PhishingSignals);

/// <summary>
/// Backend de IA desacoplado (spec §31, FASE 8-avanzada). Abstrae un LLM externo (OpenAI-compatible
/// u otro) para funciones avanzadas: traducir, redactar/respuesta sugerida, búsqueda semántica,
/// resumen y clasificación LLM. El servidor funciona SIN backend (local heurística sigue activa);
/// un backend remoto sólo se usa si está `Enabled` por configuración explícita Y el buzón dio
/// consentimiento (`Mailbox.AiConsent`). NUNCA se envía el contenido a un proveedor sin ese
/// doble consentimiento explícito (§31).
/// </summary>
public interface IMailIntelligenceBackend
{
    bool Enabled { get; }
    string Provider { get; }

    /// <summary>Resumen por LLM/toque de máxima calidad.</summary>
    Task<string> SummarizeAsync(SubjectBody input, int maxChars, CancellationToken ct = default);
    /// <summary>Traducción del cuerpo a un idioma destino (código ISO, ej. "es").</summary>
    Task<string> TranslateAsync(SubjectBody input, string toLang, CancellationToken ct = default);
    /// <summary>Redacta una respuesta sugerida al correo.</summary>
    Task<string> SuggestReplyAsync(SubjectBody input, CancellationToken ct = default);
    /// <summary>Redacta/reescribe un borrador del remitente (tono opcional).</summary>
    Task<string> DraftAsync(SubjectBody input, string? tone, CancellationToken ct = default);
    /// <summary>Clasificación LLM (categorías de negocio).</summary>
    Task<string> ClassifyAsync(SubjectBody input, CancellationToken ct = default);
    /// <summary>Búsqueda semántica: dado un query y una lista de fragmentos, devuelve los índices
    /// de los más relevantes (como strings) y una razón. (Fallback: similaridad cosena local.)</summary>
    Task<SemanticSearchResult> SemanticSearchAsync(string query, IReadOnlyList<string> docs, int topK = 5, CancellationToken ct = default);
}

public sealed record SemanticSearchResult(IReadOnlyList<string> Indices, string Reason);

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

/// <summary>
/// Consultas DNS TXT/record para autenticación de correo (FASE 4: SPF/DKIM/DMARC).
/// Abstraída para poder testear la lógica con un resolver falso sin red real.
/// </summary>
public interface IDnsRecordResolver
{
    /// <summary>Devuelve los registros TXT de un nombre (cada string es un fragmento TXT completo).</summary>
    Task<IReadOnlyList<string>> GetTxtAsync(string name, CancellationToken ct = default);
    /// <summary>Resuelve un nombre a IPs (para mecanismos SPF a/mx).</summary>
    Task<IReadOnlyList<string>> GetAddressesAsync(string hostname, CancellationToken ct = default);
}

/// <summary>
/// Autenticación de correo en recepción (FASE 4): evalúa SPF, DKIM y DMARC de un mensaje
/// y devuelve el reporte para integrarlo al scoring y a la política (spec §17-19).
/// Implementación en Infrastructure (usa la capa Security y el resolver DNS).
/// </summary>
public interface IEmailAuthenticationService
{
    Task<EmailAuthReport> AuthenticateAsync(
        string fromHeaderDomain, string envelopeFrom, string? clientIp,
        byte[] rawMime, CancellationToken ct = default);
}

/// <summary>Reporte unificado de autenticación de correo del mensaje.</summary>
public sealed record EmailAuthReport(
    string SpfResult, string? SpfRule,
    string DkimResult, string? DkimSelector, string? DkimDomain,
    string DmarcResult, string DmarcPolicy, bool DmarcAligned,
    bool ShouldReject, bool ShouldQuarantine,
    IReadOnlyList<string> AuthResults);

/// <summary>
/// Diagnóstico y gestión de autenticación de correo por dominio (spec §17-19):
/// genera/muestra el registro SPF, genera/administra el selector y claves DKIM, y muestra
/// el registro DMARC a publicar. Implementación en Infrastructure (Security + DNS).
/// </summary>
public interface IDomainMailAuthService
{
    Task<DomainMailAuthStatus> GetStatusAsync(long domainId, CancellationToken ct = default);
    Task<DomainMailAuthStatus> EnableDkimAsync(long domainId, CancellationToken ct = default);
    Task<DomainMailAuthStatus> SetDmarcPolicyAsync(long domainId, string policy, CancellationToken ct = default);
}

/// <summary>Estado DNS de autenticación de un dominio y los registros a publicar.</summary>
public sealed record DomainMailAuthStatus(
    long DomainId, string DomainName,
    bool SpfEnabled, string? SpfRecordToPublish,
    bool DkimEnabled, string? DkimSelector, string? DkimPublicKeyRecord,
    string? DmarcPolicy, string? DmarcRecordToPublish,
    string? DkimPrivateKeyHint); // nunca exponer la clave privada completa

/// <summary>
/// Servicio de cuarentena (spec §22, FASE 5). SecurityAdmin puede listar, inspeccionar,
/// liberar, eliminar y bloquear remitentes. El usuario tiene vista limitada por política.
/// </summary>
public interface IQuarantineService
{
    /// <summary>Mensajes en cuarentena (opcionalmente filtrados por dominio/buzón).</summary>
    Task<IReadOnlyList<QuarantineItem>> ListAsync(int skip = 0, int take = 50, long? mailboxId = null, CancellationToken ct = default);
    /// <summary>Inspecciona metadatos + adjuntos de un mensaje en cuarentena (sin el MIME completo).</summary>
    Task<QuarantineInspect?> InspectAsync(long messageId, CancellationToken ct = default);
    /// <summary>Libera (entrega al buzón del destinatario en la carpeta indicada; spam → Spam).</summary>
    Task<bool> ReleaseAsync(long messageId, CancellationToken ct = default);
    /// <summary>Elimina definitivamente (metadatos + blob del store) un mensaje en cuarentena.</summary>
    Task<bool> DeleteAsync(long messageId, CancellationToken ct = default);
    /// <summary>Bloquea un remitente (exacto o por dominio) y elimina los mensajes del bloqueado en cola.</summary>
    Task<bool> BlockSenderAsync(string value, SenderMatchKind kind, string? reason, string? actor, CancellationToken ct = default);
    /// <summary>Lista de remitentes bloqueados.</summary>
    Task<IReadOnlyList<BlockedSenderInfo>> ListBlockedAsync(CancellationToken ct = default);
    /// <summary>Desbloquea un remitente.</summary>
    Task<bool> UnblockAsync(long id, CancellationToken ct = default);
    /// <summary>Comprueba si una dirección de remitente coincide con la blocklist (ingesta).</summary>
    Task<IBlockedMatch?> MatchBlockedAsync(string senderAddress, CancellationToken ct = default);
}

/// <summary>Coincidencia de blocklist devuelta a la ingesta.</summary>
public interface IBlockedMatch { string Kind { get; } }

/// <summary>Elemento de la lista de cuarentena.</summary>
public sealed record QuarantineItem(
    long MessageId, long MailboxId, string Recipient, string Sender, string Subject,
    string Reason, double Score, int AttachmentCount, string WorstScan, DateTime ReceivedAtUtc, DateTime QuarantinedAtUtc);

/// <summary>Vista de inspección de un mensaje en cuarentena (sin el MIME crudo).</summary>
public sealed record QuarantineInspect(
    long MessageId, string Sender, string Subject, long SizeBytes, DateTime ReceivedAtUtc,
    string? BodyPreview, bool IsHtml, string ScanSummary,
    IReadOnlyList<QuarantineAttachment> Attachments);

public sealed record QuarantineAttachment(string FileName, string ContentType, long SizeBytes, string Sha256, string ScanStatus);

public sealed record BlockedSenderInfo(long Id, string Value, string Kind, string? Reason, DateTime CreatedAtUtc, string? CreatedBy);