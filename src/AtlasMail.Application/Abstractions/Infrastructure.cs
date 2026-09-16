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