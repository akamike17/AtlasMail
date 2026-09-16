using AtlasMail.Domain.Enums;

namespace AtlasMail.Application.Dtos;

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResult(bool Success, string? Error, string? Username, UserRole? Role, long? DomainId);

public sealed record CreateDomainRequest(string Name, long MaxMailboxQuotaBytes = 1024*1024*1024,
    int MaxRecipientsPerMessage = 100, long MaxMessageSizeBytes = 50*1024*1024,
    bool PlusAddressingEnabled = false, bool CatchAllEnabled = false, string? CatchAllTargetLocalPart = null);
public sealed record DomainDto(long Id, string Name, bool Enabled, long MaxMailboxQuotaBytes, int MailboxCount);

public sealed record CreateMailboxRequest(long DomainId, string LocalPart, string DisplayName, string Password);
public sealed record MailboxDto(long Id, long DomainId, string EmailAddress, string DisplayName, MailboxStatus Status, long QuotaBytes, long UsedBytes);

public sealed record CreateAliasRequest(long DomainId, string LocalPart, long? TargetMailboxId);
public sealed record AliasDto(long Id, string AliasedAddress, string? Target, bool Enabled);

public sealed record CreateUserRequest(string Username, string Password, string DisplayName, UserRole Role, long? DomainId);
public sealed record UserDto(long Id, string Username, string DisplayName, UserRole Role, bool Enabled);

public sealed record ComposeMessageRequest(string FromUsername, IEnumerable<string> To, IEnumerable<string> Cc,
    string Subject, string Body, IEnumerable<ComposeAttachmentDto>? Attachments = null);
public sealed record ComposeAttachmentDto(string FileName, string ContentType, byte[] Data);

public sealed record MailboxMessageListItem(long Id, string SenderAddress, string? SenderName, string Subject,
    DateTime DateUtc, string BodyPreview, bool IsHtml, bool IsFlagged, bool IsRead, long SizeBytes, bool HasAttachments);

public sealed record MailboxMessageDetail(long Id, long FolderId, string SenderAddress, string? SenderName,
    string Subject, DateTime DateUtc, string Body, bool IsHtml, bool IsRead, bool IsFlagged,
    IReadOnlyList<RecipientDto> Recipients, IReadOnlyList<AttachmentMetaDto> Attachments);

public sealed record RecipientDto(RecipientType Type, string Address, string? DisplayName);
public sealed record AttachmentMetaDto(long Id, string FileName, string ContentType, long SizeBytes, string Sha256);

public sealed record QueueListItem(long Id, string EnvelopeFrom, string EnvelopeTo, string SubjectHint,
    DeliveryState State, int Attempts, int MaxAttempts, DateTime? NextAttemptAtUtc, DateTime CreatedAtUtc, string? LastError);

public sealed record MessageTraceItem(long Id, string? MessageIdHeader, string EnvelopeFrom, string EnvelopeTo,
    string? Subject, DateTime CreatedAtUtc, DeliveryState State, int Attempts, string? LastError, IReadOnlyList<TraceAttempt> AttemptsList);

public sealed record TraceAttempt(int Number, DateTime StartedUtc, DateTime? FinishedUtc, bool Success, string? RemoteResponse, string? Error);

public sealed record AuditItem(long Id, DateTime TimestampUtc, string? Actor, string? IpAddress, string Action,
    string? Target, string? TargetId, string? Result, string? Metadata);

public sealed record FolderDto(long Id, string Name, SystemFolder? SystemName, int SortOrder, int MessageCount, int UnreadCount);

public sealed record BackupDescriptor(DateTime TimestampUtc, string BackupId, string ManifestPath, long MessageBytes, int DatabaseTables, string Sha256);

public sealed record SpamDecisionResult(SpamDecision Decision, double Score, string Reason);

public sealed record RegistryAddress(string LocalPart, string DomainName, bool Found, long? MailboxId, bool IsAlias, bool IsCatchAll, string? ResolvedLocalPart);