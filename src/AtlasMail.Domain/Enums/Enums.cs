namespace AtlasMail.Domain.Enums;

/// <summary>Roles del sistema. Mínimo privilegio; SuperAdmin es el único que crea dominios.</summary>
public enum UserRole
{
    User = 0,
    HelpDesk = 1,
    SecurityAdmin = 2,
    DomainAdmin = 3,
    SuperAdmin = 4
}

/// <summary>Estado de un buzón.</summary>
public enum MailboxStatus
{
    Active = 0,
    Disabled = 1,
    SoftDeleted = 2,
    OverQuota = 3
}

/// <summary>Estado de la cola de salida / entrega.</summary>
public enum DeliveryState
{
    Pending = 0,
    Processing = 1,
    Deferred = 2,
    Delivered = 3,
    Failed = 4,
    DeadLetter = 5
}

/// <summary>Resultado del análisis antispam.</summary>
public enum SpamDecision
{
    None = 0,
    Allow = 1,
    Tag = 2,
    Spam = 3,
    Quarantine = 4,
    Reject = 5
}

/// <summary>Resultado de un escaneo de adjunto.</summary>
public enum AttachmentScanStatus
{
    Clean = 0,
    Suspicious = 1,
    Malicious = 2,
    Unknown = 3,
    ScannerUnavailable = 4
}

/// <summary>Modo de coincidencia para bloqueo de remitente (FASE 5).</summary>
public enum SenderMatchKind
{
    Exact = 0,
    Domain = 1
}

/// <summary>Carpetas estándar por buzón.</summary>
public enum SystemFolder
{
    Inbox = 0,
    Sent = 1,
    Drafts = 2,
    Trash = 3,
    Spam = 4,
    Archive = 5
}

/// <summary>Tipo de destinatario del sobre (envelope).</summary>
public enum RecipientType
{
    To = 0,
    Cc = 1,
    Bcc = 2
}

/// <summary>Resultado de la verificación de autenticación al entregar.</summary>
public enum AuthenticationStatus
{
    None = 0,
    Success = 1,
    Failed = 2,
    Error = 3
}