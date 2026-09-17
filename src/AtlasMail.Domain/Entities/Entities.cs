using AtlasMail.Domain.Enums;
using System.Text.Json.Serialization;

namespace AtlasMail.Domain.Entities;

/// <summary>Un dominio de correo (multidominio). Posee configuración DNS/seguridad.</summary>
public class Domain
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // Seguridad / DNS (estado; no valores verificados en caliente aún)
    public bool DkimEnabled { get; set; }
    public string? DkimSelector { get; set; }
    public string? DkimPrivateKey { get; set; }
    public string? SpfRecord { get; set; }
    public string? DmarcPolicy { get; set; }
    public string? MxRecord { get; set; }
    public string? TlsPolicy { get; set; }

    // Límites
    public long MaxMailboxQuotaBytes { get; set; } = 1024 * 1024 * 1024; // 1 GiB
    public int MaxRecipientsPerMessage { get; set; } = 100;
    public long MaxMessageSizeBytes { get; set; } = 50 * 1024 * 1024; // 50 MiB

    // Opciones
    public bool PlusAddressingEnabled { get; set; }
    public bool CatchAllEnabled { get; set; }
    public string? CatchAllTargetLocalPart { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public ICollection<Mailbox> Mailboxes { get; } = new List<Mailbox>();
    [JsonIgnore] public ICollection<Alias> Aliases { get; } = new List<Alias>();
}

/// <summary>Identidad de acceso (login). Separada conceptualmente del buzón.</summary>
public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;      // login, e.g. "alice"
    public string PasswordHash { get; set; } = string.Empty;  // nunca reversible
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public bool Enabled { get; set; } = true;
    public long? DomainId { get; set; }                        // nulo para SuperAdmin global
    public Domain? Domain { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Buzón local: posee carpetas y recibe mensajes.</summary>
public class Mailbox
{
    public long Id { get; set; }
    public long DomainId { get; set; }
    public Domain? Domain { get; set; }
    public string LocalPart { get; set; } = string.Empty;    // "alice"
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty; // hash de acceso SMTP/IMAP
    public MailboxStatus Status { get; set; } = MailboxStatus.Active;
    public long QuotaBytes { get; set; } = 1024 * 1024 * 1024;
    public long UsedBytes { get; set; }
    public DateTime? LastAccessAt { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Consentimiento explícito del buzón para enviar su contenido a un backend de IA remoto
    /// (FASE 8, §31). Falso por defecto: nunca se envía contenido a un proveedor sin este + `Ai:Backend:Enabled`.</summary>
    public bool AiConsent { get; set; }

    [JsonIgnore] public string EmailAddress => $"{LocalPart}@{Domain?.Name}";

    [JsonIgnore] public ICollection<Folder> Folders { get; } = new List<Folder>();
    [JsonIgnore] public ICollection<Alias> Aliases { get; } = new List<Alias>();
}

/// <summary>Alias: varias direcciones apuntan al mismo buzón.</summary>
public class Alias
{
    public long Id { get; set; }
    public long DomainId { get; set; }
    public Domain? Domain { get; set; }
    public string LocalPart { get; set; } = string.Empty;
    public long? TargetMailboxId { get; set; }
    public Mailbox? TargetMailbox { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string AliasedAddress => $"{LocalPart}@{Domain?.Name}";
}

/// <summary>Carpeta de un buzón. Las estándar viajan con SystemName; las custom son null.</summary>
public class Folder
{
    public long Id { get; set; }
    public long MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }
    public string Name { get; set; } = string.Empty;
    public SystemFolder? SystemName { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public ICollection<Message> Messages { get; } = new List<Message>();
}

/// <summary>Metadatos de un mensaje almacenado. El MIME crudo vive en IMessageStore.</summary>
public class Message
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public Folder? Folder { get; set; }
    public string StoreKey { get; set; } = string.Empty;     // clave en el IMessageStore
    public string? MessageIdHeader { get; set; }
    public string SenderAddress { get; set; } = string.Empty;
    public string? SenderName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public long SizeBytes { get; set; }
    public string? BodyPreview { get; set; }
    public bool IsHtml { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public SpamDecision SpamDecision { get; set; } = SpamDecision.None;
    public double SpamScore { get; set; }
    public string? InReplyTo { get; set; }
    /// <summary>Flag \\Deleted de IMAP: marcado por STORE +Deleted y purgado por EXPUNGE.</summary>
    public bool IsDeleted { get; set; }
    /// <summary>En cuarentena (FASE 5): no visible en el buzón hasta liberarlo.</summary>
    public bool IsQuarantined { get; set; }
    /// <summary>Motivo de cuarentena (score, malware, DMARC, over-quota).</summary>
    public string? QuarantineReason { get; set; }
    /// <summary>Cuándo se puso en cuarentena.</summary>
    public DateTime? QuarantinedAtUtc { get; set; }

    [JsonIgnore] public ICollection<MessageRecipient> Recipients { get; } = new List<MessageRecipient>();
    [JsonIgnore] public ICollection<Attachment> Attachments { get; } = new List<Attachment>();
}

/// <summary>Destinatarios (To/Cc/Bcc) de un mensaje almacenado.</summary>
public class MessageRecipient
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public Message? Message { get; set; }
    public RecipientType Type { get; set; }
    public string Address { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

/// <summary>Metadatos de adjunto (el contenido vive en el MIME crudo). SHA-256 calculado.</summary>
public class Attachment
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public Message? Message { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public AttachmentScanStatus ScanStatus { get; set; } = AttachmentScanStatus.Unknown;
}

/// <summary>Elemento de la cola de salida entrega (con lease/claim transaccional).</summary>
public class DeliveryQueueItem
{
    public long Id { get; set; }
    public string EnvelopeFrom { get; set; } = string.Empty;   // MAIL FROM
    public string EnvelopeTo { get; set; } = string.Empty;     // RCPT TO
    public string StoreKey { get; set; } = string.Empty;
    public string? MessageIdHeader { get; set; }
    public DeliveryState State { get; set; } = DeliveryState.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 6;
    public DateTime? NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public string? RemoteServer { get; set; }
    public string? ClaimedBy { get; set; }       // worker lease
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public bool IsLocal { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public ICollection<DeliveryAttempt> AttemptHistory { get; } = new List<DeliveryAttempt>();
}

/// <summary>Intento de entrega (trazabilidad completa).</summary>
public class DeliveryAttempt
{
    public long Id { get; set; }
    public long QueueItemId { get; set; }
    public DeliveryQueueItem? QueueItem { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public string? RemoteResponse { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>Evento de auditoría. Nunca guardar secretos.</summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? Actor { get; set; }
    public string? ActorId { get; set; }
    public string? IpAddress { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? TargetId { get; set; }
    public string? Result { get; set; }
    public string? Metadata { get; set; }
}

/// <summary>Intento de login (detección de fuerza bruta).</summary>
public class LoginAttempt
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? Username { get; set; }
    public string? IpAddress { get; set; }
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string UserAgent { get; set; } = string.Empty;
}

/// <summary>Contacto de usuario.</summary>
public class Contact
{
    public long Id { get; set; }
    public long? DomainId { get; set; }
    public Domain? Domain { get; set; }
    /// <summary>Buzón dueño (contacto personal) si no es de dominio.</summary>
    public long? OwnerMailboxId { get; set; }
    public Mailbox? OwnerMailbox { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Clave/valor de configuración del servidor.</summary>
public class ConfigurationEntry
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Remitente bloqueado (FASE 5): la ingesta rechaza mensajes que coinciden.</summary>
public class BlockedSender
{
    public long Id { get; set; }
    /// <summary>Dirección completa o dominio (según MatchKind).</summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>Exact = address; Domain = todo un dominio.</summary>
    public SenderMatchKind MatchKind { get; set; } = SenderMatchKind.Exact;
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

/// <summary>Calendario de un buzón (FASE 6, spec §15).</summary>
public class Calendar
{
    public long Id { get; set; }
    public long MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }
    public string Name { get; set; } = "Mi calendario";
    public string Color { get; set; } = "#2962ff";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public ICollection<CalendarEvent> Events { get; } = new List<CalendarEvent>();
}

/// <summary>Estado de un evento (RFC 5545 STATUS).</summary>
public enum CalendarEventDisplayStatus
{
    Confirmed = 0,
    Tentative = 1,
    Cancelled = 2
}

/// <summary>Estado de asistencia de un attendee (RFC 5545 PARTSTAT).</summary>
public enum AttendeeParticipationStatus
{
    NeedsAction = 0,
    Accepted = 1,
    Declined = 2,
    Tentative = 3
}

/// <summary>Evento de calendario (FASE 6, spec §15).</summary>
public class CalendarEvent
{
    public long Id { get; set; }
    public long CalendarId { get; set; }
    public Calendar? Calendar { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>IANA timezone, ej. "America/Mexico_City". </summary>
    public string TimeZoneId { get; set; } = "UTC";
    public string? OrganizerEmail { get; set; }
    /// <summary>Regla RRULE de recurrencia (ej. "FREQ=WEEKLY;BYDAY=MO") o null si no recurre.</summary>
    public string? RecurrenceRule { get; set; }
    public CalendarEventDisplayStatus Status { get; set; } = CalendarEventDisplayStatus.Confirmed;
    public bool IsAllDay { get; set; }
    /// <summary>Si proviene de un .ics externo, su UID original (para idempotencia de import).</summary>
    public string? ExternalUid { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public ICollection<CalendarEventAttendee> Attendees { get; } = new List<CalendarEventAttendee>();
}

/// <summary>Attendee de un evento (FASE 6).</summary>
public class CalendarEventAttendee
{
    public long Id { get; set; }
    public long CalendarEventId { get; set; }
    public CalendarEvent? CalendarEvent { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public AttendeeParticipationStatus ParticipationStatus { get; set; } = AttendeeParticipationStatus.NeedsAction;
}

/// <summary>Cómo se aplica una política de envío a una lista de distribución (FASE 6, spec §16).</summary>
public enum DistributionSendPolicy
{
    /// <summary>Quien tiene un buzón local en el servidor puede enviar.</summary>
    InternalOnly = 0,
    /// <summary>Cualquiera puede enviar (pero aún sujeto a moderación si está activa).</summary>
    ExternalAllowed = 1
}

/// <summary>Lista de distribución (ventas@empresa.mx → ana@, juan@, maria@), spec §16.</summary>
public class DistributionList
{
    public long Id { get; set; }
    public long DomainId { get; set; }
    public Domain? Domain { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Local part de la dirección de la lista.</summary>
    public string LocalPart { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public DistributionSendPolicy SendPolicy { get; set; } = DistributionSendPolicy.InternalOnly;
    /// <summary>Si true, un admin/SecurityAdmin debe aprobar antes de que se distribuya.</summary>
    public bool ModerationEnabled { get; set; }
    /// <summary>Límite de destinatarios expandidos por mensaje; 0 = sin límite explícito.</summary>
    public int MaxMembers { get; set; } = 0;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public ICollection<DistributionListMember> Members { get; } = new List<DistributionListMember>();
}

/// <summary>Miembro de una lista de distribución (spec §16).</summary>
public class DistributionListMember
{
    public long Id { get; set; }
    public long DistributionListId { get; set; }
    public DistributionList? DistributionList { get; set; }
    public string AddressOrLocalPart { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}