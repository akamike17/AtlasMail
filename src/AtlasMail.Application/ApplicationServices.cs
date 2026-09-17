using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Enums;

namespace AtlasMail.Application;

/// <summary>Servicio de autenticación de usuarios (web).</summary>
public interface IAuthService
{
    Task<LoginResult> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    /// <summary>Protección brute force: ¿bloqueado temporalmente el origen?</summary>
    bool IsBlocked(string? ipAddress, string? username);
}

/// <summary>Administración: dominios, buzones, alias, usuarios.</summary>
public interface IAdminService
{
    Task<long> CreateDomainAsync(CreateDomainRequest request, string actor, string? ip, CancellationToken ct = default);
    Task<IReadOnlyList<DomainDto>> ListDomainsAsync(CancellationToken ct = default);
    Task SetDomainEnabledAsync(long domainId, bool enabled, string actor, string? ip, CancellationToken ct = default);

    Task<long> CreateMailboxAsync(CreateMailboxRequest request, string actor, string? ip, CancellationToken ct = default);
    Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(long? domainId, CancellationToken ct = default);
    Task SetMailboxStatusAsync(long mailboxId, MailboxStatus status, string actor, string? ip, CancellationToken ct = default);

    Task<long> CreateAliasAsync(CreateAliasRequest request, string actor, string? ip, CancellationToken ct = default);
    Task<IReadOnlyList<AliasDto>> ListAliasesAsync(long? domainId, CancellationToken ct = default);

    Task<long> CreateUserAsync(CreateUserRequest request, string actor, string? ip, CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct = default);
}

/// <summary>
/// Registro de direcciones locales: resuelve un sobre destinatario a un buzón local,
/// aplicando alias, plus addressing y catch-all (secciones 4, 41, 42).
/// </summary>
public interface IAddressResolutionService
{
    Task<RegistryAddress> ResolveAsync(string address, CancellationToken ct = default);
    Task<bool> IsDomainLocalAsync(string domainName, CancellationToken ct = default);
}

/// <summary>
/// Ingesta SMTP local: dado un sobre aceptado, persiste el mensaje, aplica seguridad/reglas
/// y entrega localmente (Pipeline sección 6).
/// </summary>
public interface IInboundDeliveryService
{
    Task<InboundResult> IngestAsync(string envelopeFrom, string envelopeTo, byte[] rawMime, string? helo,
        string? clientIp, bool authenticated, string actor, CancellationToken ct = default);
}

public enum InboundResult
{
    Accepted,
    Rejected,
    Quarantined,
    MovedToSpam,
    RelayDenied
}

/// <summary>Cola de salida con claim/lease transaccional (secciones 7, 37).</summary>
public interface IOutboundQueueService
{
    Task<long> EnqueueAsync(string envelopeFrom, string envelopeTo, string storeKey, string? messageIdHeader,
        bool isLocal, CancellationToken ct = default);
    Task<DeliveryQueueItemClaim?> ClaimNextAsync(string workerId, CancellationToken ct = default);
    /// <summary>Número de items pendientes/reintentables en la cola (observabilidad, sin datos sensibles).</summary>
    Task<long> CountPendingAsync(CancellationToken ct = default);
    Task CompleteAsync(long queueItemId, string remoteResponse, CancellationToken ct = default);
    Task DeferAsync(long queueItemId, string error, string? remoteResponse, CancellationToken ct = default);
    Task FailAsync(long queueItemId, string error, string? remoteResponse, bool deadLetter, CancellationToken ct = default);
    Task<IReadOnlyList<QueueListItem>> ListAsync(int take = 100, CancellationToken ct = default);
}

public sealed record DeliveryQueueItemClaim(long QueueItemId, string EnvelopeFrom, string EnvelopeTo, string StoreKey, string? MessageIdHeader);

/// <summary>Envío desde el webmail: compose -> MIME -> store -> cola (sección 7).</summary>
public interface ISubmissionService
{
    Task<ComposeResult> SubmitAsync(ComposeMessageRequest request, string actor, string? ip, CancellationToken ct = default);
}

public sealed record ComposeResult(bool Success, string? Error, long? QueueItemId, string? StoreKey);

/// <summary>Lectura del buzón por usuario autenticado (aislado por mailboxId).</summary>
public interface IMailboxService
{
    Task<IReadOnlyList<FolderDto>> ListFoldersAsync(long mailboxId, CancellationToken ct = default);
    Task<IReadOnlyList<MailboxMessageListItem>> ListFolderMessagesAsync(long mailboxId, long folderId, int take = 100, CancellationToken ct = default);
    Task<MailboxMessageDetail?> ReadMessageAsync(long mailboxId, long messageId, CancellationToken ct = default);
    Task SetReadAsync(long mailboxId, long messageId, bool read, CancellationToken ct = default);
    Task SetFlaggedAsync(long mailboxId, long messageId, bool flagged, CancellationToken ct = default);
    Task MoveAsync(long mailboxId, long messageId, long destinationFolderId, CancellationToken ct = default);
    Task<byte[]> ReadRawAsync(long mailboxId, long messageId, CancellationToken ct = default);
}

/// <summary>Búsqueda de mensajes (sección 12) con aislamiento por buzón.</summary>
public interface IMailSearchService : IMessageSearchService
{
}

/// <summary>Message trace (sección 27): timeline de un mensaje.</summary>
public interface IMessageTraceService
{
    Task<IReadOnlyList<MessageTraceItem>> SearchAsync(string? messageId, string? sender, string? recipient, DateTime? fromDate, DateTime? toDate, int take = 100, CancellationToken ct = default);
}

/// <summary>Espejo del motor de reglas (sección 13) — wire simple con la ingesta.</summary>
public interface IRuleEngine
{
    Task<(string? FolderName, bool MoveToSpam)> EvaluateAsync(string envelopeFrom, string envelopeTo, string subject, CancellationToken ct = default);
}

/// <summary>Registros de auditoría (sección 25).</summary>
public interface IAuditService
{
    Task RecordAsync(string action, string? actor, string? actorId, string? ip, string? target, string? targetId, string? result, string? metadata = null, CancellationToken ct = default);
    Task<IReadOnlyList<AuditItem>> ListRecentAsync(int take = 200, CancellationToken ct = default);
}

/// <summary>Backup/restore lógico (secciones 28, 48).</summary>
public interface IBackupService
{
    Task<BackupDescriptor> CreateBackupAsync(string actor, CancellationToken ct = default);
    Task<RestoreResult> RestoreAsync(string backupId, string actor, CancellationToken ct = default);
}

public sealed record RestoreResult(bool Success, string? Error, string? BackupId, int RestoredTables);

/// <summary>Dashboard administrativo (sección 26).</summary>
public interface IAdminDashboardService
{
    Task<DashboardDto> GetAsync(CancellationToken ct = default);
}

public sealed record QueueHealth(long Pending, long Processing, long Deferred, long Failed);
public sealed record DashboardDto(int DomainCount, int UserCount, int MailboxCount, long TotalMessages, long StorageBytes,
    QueueHealth Queue, int SpamCount, int QuarantineCount, int AuditCount);