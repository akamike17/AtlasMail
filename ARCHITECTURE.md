# AtlasMail — Arquitectura

Servidor empresarial de correo y colaboración self-hosted. Multi-dominio.
**Ciclo 1**: vertical slice funcional (ADMIN → dominio → buzones → webmail → SMTP → cola → trace → backup → audit).
**FASE 2**: SMTP robusto + entrega externa (MX/DNS real, STARTTLS, AUTH, bounces seguros, rate limits, observabilidad).
**FASE 3**: IMAP4rev1 y clientes externos (servidor IMAP desacoplado del almacenamiento, migración `ImapDeletedFlag`).

## Stack
- ASP.NET Core 8 (MVC + Razor + JS `fetch()`), Bootstrap local (sin CDN).
- EF Core 8 + Pomelo **MySQL/MariaDB** (la única persistencia de metadatos).
- MIME crudo en **filesystem** mediante `IMessageStore` (nunca VARCHAR gigante en MySQL).
- .NET 8 LTS (SDK 8.0.425 instalado).

## Proyectos (modular, dependencias en una dirección)
```
src/AtlasMail.Domain         Entidades, enums, EmailAddress, reglas puras (relay, cuota, retry), parser/builder MIME.
src/AtlasMail.Application    IApplicationDbContext (contrato), DTOs, servicios de caso de uso, abstracciones IMessageStore/ISearch/IScanner/IIA.
src/AtlasMail.Infrastructure EF Core (AtlasMailDbContext + Pomelo), FileSystemMessageStore, seed/migraciones, DI registrar.
src/AtlasMail.Security       PBKDF2 IPasswordHasher + IPasswordPolicy.
src/AtlasMail.Protocols      Servidor SMTP (state machine RFC 5321) + Cliente SMTP (outbound) + servidor IMAP4rev1 (RFC 3501).
src/AtlasMail.Web            Program.cs, controllers MVC + [ApiController], vistas Razor, antiforgery/deny-by-default, hosted SMTP.
src/AtlasMail.Worker         DeliveryWorker: cola de salida con lease/claim + retry/backoff.
tests/AtlasMail.UnitTests    Lógica pura + servicios con FakeAppDbContext (InMemory).
tests/AtlasMail.IntegrationTests  MySQL temporal dedicado via WebApplicationFactory + SMTP E2E real.
```
Dependencias: Domain → (nada); Application → Domain; Infrastructure → Application+Domain+Security;
Protocols → Infrastructure+Application; Security → Application+Domain; Worker → Infrastructure+Protocols;
Web → Infrastructure+Application+Protocols+Security+Worker.

## IMAP (spec §10) — FASE 3
- `AtlasMail.Protocols.Imap.ImapServer` (state machine multihilo, RFC 3501) + `ImapSession`.
- Desacoplado del almacenamiento: `IMailboxBackend` (Application) → `MySqlMailboxBackend` (Infrastructure)
  sobre metadatos MySQL + `IMessageStore`. El protocolo IMAP no conoce el modelo de datos.
- Comandos FASE 3: LOGIN, CAPABILITY, NOOP, LIST/LSUB, SELECT/EXAMINE, STATUS, FETCH (flags, uid, tamaño,
  INTERNALDATE, BODY[]/RFC822.HEADER/BODY[TEXT]), STORE (\Seen \Flagged \Deleted, SILENT, replace), SEARCH,
  MOVE, UID, EXPUNGE, CLOSE, LOGOUT. APPEND no-persistente (documentado).
- `Message.IsDeleted` para el flag \Deleted (migración `ImapDeletedFlag`).
- Compatibilidad con Thunderbird/Outlook/Apple Mail pendiente de prueba real (§44).
- Hosted service `ImapHostedService` inicia el listener (puerto 143 o `Imap:Port`).

## Persistencia y almacenamiento de mensajes (spec §1)
- **No** se guarda MIME completo en MySQL.
- Metadatos/índices/cola/auditoría → MySQL (`AtlasMailDbContext`).
- MIME crudo → `IMessageStore` → `FileSystemMessageStore` (por archivo, key = GUID).
- Preparado: `ObjectStorageMessageStore` como implementación futura sin tocar Application.

## Pipeline de ingesta SMTP local (spec §6)
```
CONNECT → POLICY(relay) → ENVELOPE(MAIL/RCPT) → DATA → MIME PARSE → SECURITY(spam) → RULES
        → STORE(filesystem) → INDEX(metadatos MySQL) → DELIVER LOCAL (Inbox/Spam) → AUDIT
```
- Persiste antes de confirmar `250` (no pérdida por crash).
- La recepción NO depende del webmail.

## Cola de salida (spec §7, 37, 38)
- `COMPOSE → SUBMIT → QUEUE → WORKER → ENTREGA → RESULT`.
- Estados: Pending/Processing/Deferred/Delivered/Failed/DeadLetter.
- Lease/claim transaccional (worker reclama atómicamente; lease caduca → crash recovery).
- Retry con backoff exponencial, tope, sin loops infinitos, dead-letter.
- **FASE 2**: entrega externa real en el worker. Resuelve MX (`IMxResolver`/DnsClient, fallback A),
  entrega con `SmtpClient` (STARTTLS oportunista/obligatorio, AUTH opcional), clasifica 4xx (reintenta)
  vs 5xx (falla y genera bounce/DSN seguro sin backscatter). Rate limits por usuario/dominio
  (`SlidingWindowRateLimiter`). IExternalMailSender abstrae el SmtpClient para la capa Application.

## Seguridad (spec §4, 33)
- **Deny-by-default**: filtro global `RequireAuthenticatedUser`; `[AllowAnonymous]` sólo en login/token/health.
- Cookies httpOnly, antiforgery vía header `X-CSRF-TOKEN` (regenerada tras login).
- Roles: SuperAdmin / DomainAdmin / SecurityAdmin / HelpDesk / User; mínimo privilegio.
- IDOR controlado: webmail filtra por `mailboxId` del usuario logueado; servicios por-ID incluyen el filtro de buzón.
- Passwords: PBKDF2 (nunca reversibles); política de fuerza (8+ mayús/dígito/especial).
- Relay: NO OPEN RELAY (sólo dominios locales; autenticado autorizado puede enviar externo).
- Secure headers (nosniff, frame-deny, CSP, referrer).
- Respuestas 404 vs 403 sin filtrar existencia.

## Modelo multi-dominio (spec §3)
`Domain`, `Mailbox`, `Alias`, `User`, carpetas estándar por buzón (Inbox/Sent/Drafts/Trash/Spam/Archive),
alias → buzón, plus addressing, catch-all (deshabilitado por defecto).

## Flujo del Ciclo 1 validado (smoke E2E, espec 50)
ADMIN crea dominio → ADMIN crea Alice/Bob → Alice login web → Alice redacta → AtlasMail guarda/procesa →
Bob Inbox → Bob lee → SMTP externo real → Bob (250 OK) → relay no autorizado → DENEGADO → cola observable →
trace observable → backup creado + restore temporal → audit registrado → build/tests PASS.