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
    /// <summary>Flag \Deleted de IMAP: marcado por STORE +Deleted y purgado por EXPUNGE.</summary>
    public bool IsDeleted { get; set; }

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