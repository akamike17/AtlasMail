using AtlasMail.Domain.Enums;

namespace AtlasMail.Application.Abstractions;

/// <summary>
/// Backend de buzón desacoplado para IMAP (spec §10): el protocolo IMAP NO conoce
/// el modelo MySQL; opera contra esta abstracción. Implementación local en
/// Infrastructure. Permite cambiar el almacenamiento sin tocar el servidor IMAP.
/// </summary>
public interface IMailboxBackend
{
    /// <summary>
    /// Autentica un usuario IMAP contra un buzón local. Devuelve el mailboxId o null.
    /// El password verifica contra el hash del buzón (PBKDF2, nunca reversible).
    /// </summary>
    Task<MailboxLoginResult?> AuthenticateAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Lista las carpetas de un buzón visto por el cliente IMAP.</summary>
    Task<IReadOnlyList<ImapFolder>> ListFoldersAsync(long mailboxId, CancellationToken ct = default);

    /// <summary>
    /// Selecciona una carpeta. Devuelve el snapshot (mensajes + UIDVALIDITY) o null
    /// si el nombre no existe. Los UID son estables y únicos dentro de la carpeta.
    /// </summary>
    Task<ImapFolderSnapshot?> SelectFolderAsync(long mailboxId, string folderName, CancellationToken ct = default);

    /// <summary>Devuelve el MIME crudo de un mensaje por UID dentro de una carpeta.</summary>
    Task<byte[]?> FetchRawAsync(long mailboxId, long folderId, long uid, CancellationToken ct = default);

    /// <summary>Actualiza flags (seen/flagged/deleted) de mensajes por UID.</summary>
    Task SetFlagsAsync(long mailboxId, long folderId, IReadOnlyList<long> uids, bool? seen, bool? flagged, bool? deleted, CancellationToken ct = default);

    /// <summary>Mueve un mensaje a otra carpeta. Devuelve el nuevo UID o null.</summary>
    Task<long?> MoveAsync(long mailboxId, long folderId, long uid, string destinationFolder, CancellationToken ct = default);

    /// <summary>Elimina permanentemente los mensajes de una carpeta marcados como \Deleted (EXPUNGE).</summary>
    Task ExpungeAsync(long mailboxId, long folderId, CancellationToken ct = default);
}

/// <summary>Resultado de autenticación IMAP.</summary>
public sealed record MailboxLoginResult(long MailboxId, string EmailAddress, long? DomainId);

/// <summary>Carpeta vista por el cliente IMAP (nombre con delimitador jerárquico).</summary>
public sealed record ImapFolder(string Name, char Delimiter, bool HasChildren, SystemFolder? SystemName);

/// <summary>Snapshot de una carpeta seleccionada (estado inmutable para el comando SELECT).</summary>
public sealed record ImapFolderSnapshot(
    long FolderId,
    string Name,
    long UidValidity,
    int Exists,
    int Recent,
    int Unseen,
    IReadOnlyList<ImapMessage> Messages);

/// <summary>Metadatos de un mensaje IMAP (por no descargar todos los cuerpos al SELECT).</summary>
public sealed record ImapMessage(
    int Seq,          // número de secuencia (1-based, orden estable por id)
    long Uid,
    long SizeBytes,
    DateTime InternalDateUtc,
    bool Seen,
    bool Flagged,
    bool Deleted,
    string? Subject,
    string? MessageIdHeader,
    string? InReplyTo);