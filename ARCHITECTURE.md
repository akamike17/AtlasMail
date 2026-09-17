# AtlasMail — Arquitectura

Servidor empresarial de correo y colaboración self-hosted. Multi-dominio.
**Ciclo 1**: vertical slice funcional (ADMIN → dominio → buzones → webmail → SMTP → cola → trace → backup → audit).
**FASE 2**: SMTP robusto + entrega externa (MX/DNS real, STARTTLS, AUTH, bounces seguros, rate limits, observabilidad).
**FASE 3**: IMAP4rev1 y clientes externos (servidor IMAP desacoplado del almacenamiento, migración `ImapDeletedFlag`).
**FASE 4**: autenticación de correo SPF/DKIM/DMARC (recepción + firma DKIM en salida + admin DNS).
**FASE 5**: antispam/quarantine/antimalware (scanner heurístico real, cuarentena administrable, blocklist).
**FASE 6**: calendar/contacts/groups (calendario .ics, contactos VCARD/CSV, listas de distribución con políticas y anti-loop).
**FASE 7**: HA/observabilidad avanzada (worker concurrente con lease/claim, métricas sin datos sensibles, /api/metrics).
**FASE 8**: IA opcional local (prioridad/clasificación/phishing asistido/resumen, sin API externa).

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

## Autenticación de correo (spec §17-19) — FASE 4
- `AtlasMail.Security.EmailAuth` (módulos puros y testeables):
  - `SpfEvaluator` (RFC 7208): mecanismos ip4/ip6/a/mx/include/exists/all, qualifiers, redirect, macros.
  - `Dkim` (RFC 6376): firma/verificación RSA-SHA256, canonicalización relaxed/simple, generación de claves.
  - `DmarcEvaluator` (RFC 7489): alineación SL/DKIM relaxed/strict + registrable-domain.
- Infrastructure:
  - `DnsRecordResolver` (consultas TXT/A/AAAA para SPF/DKIM/DMARC).
  - `EmailAuthenticationService`: orquesta SPF+DKIM+DMARC en recepción y produce `EmailAuthReport`
    (resultados auditables + `ShouldReject`/`ShouldQuarantine`).
  - `DomainMailAuthService`: por dominio genera/muestra registros SPF/DKIM/DMARC a publicar.
  - `DkimOutboundSigner`: firma MIME en salida si el dominio del remitente tiene DKIM habilitado.
- Integración: la ingesta (`InboundDeliveryService`) suma señales de auth al spam score y aplica
  política DMARC (`Delivery:DmarcEnforce`). El worker firma DKIM antes de entregar externamente.
- Endpoints admin (SuperAdmin): `GET domain/{id}/auth`, `POST domain/{id}/auth/dkim/enable`,
  `POST domain/{id}/auth/dmarc`.

## Antispam / antimalware / cuarentena (spec §20-22) — FASE 5
- **Antimalware**: `AtlasMail.Security.Antimalware.HeuristicAttachmentScanner` (spec §21). Heurística local
  (sin API comercial): extensiones de riesgo, double-extension, magic bytes (MZ/ELF/Mach-O/PDF/PNG), macros
  VBA en Office-OOXML (`vbaProject.bin`), HTML ofuscado. Devuelve Clean/Suspicious/Malicious/Unknown/
  ScannerUnavailable; **nunca Unknown→Clean**. `IAttachmentScanner` permite pluguear ClamAV u otro motor.
- **Integración**: la ingesta escanea cada adjunto (llena `Attachment.ScanStatus`); Malicious →
  cuarentena `malware`, Suspicious sube el score. `SpamDecision` se calcula con spam+auth+malware.
- **Cuarentena (§22)**: `Message.IsQuarantined`/`QuarantineReason`/`QuarantinedAtUtc`.
  `QuarantineService` (Application) — SecurityAdmin puede listar, inspeccionar (metadatos+adjuntos sin
  MIME completo), **liberar**, **eliminar** (metadata+blob) y **bloquear remitente** (exacto o dominio).
  La blocklist (`BlockedSender`) se comprueba en la ingesta y rechaza antes de persistir.
- **Endpoints (SecurityAdmin/SuperAdmin)**: `GET /quarantine`, `GET /quarantine/{id}`,
  `POST /quarantine/{id}/release`, `DELETE /quarantine/{id}`, `POST /quarantine/block`,
  `GET /quarantine/blocked`, `DELETE /quarantine/blocked/{id}`.
- Migración EF `QuarantineBlockSenders` (tabla `BlockedSenders` + campos de cuarentena).

## Colaboración: calendario / contactos / grupos (spec §14-16) — FASE 6
- `Application/Services/CalendarService.cs`: eventos por buzón; export/import `.ics` (RFC 5545), idempotente
  por ExternalUid. `ContactService.cs`: contactos personales/dominio; VCARD (RFC 6350) y CSV.
  `GroupService.cs`: listas de distribución con políticas y expansión anti-loop.
- Entidades: `Calendar`, `CalendarEvent`, `CalendarEventAttendee`, `DistributionList`, `DistributionListMember`;
  `Contact.OwnerMailboxId` para listas personales.
- Integración SMTP: la dirección que es una lista local se acepta en el RCPT y se expande en la ingesta,
  entregando copia a cada miembro con buzón local (audit `Smtp.List`).
- Endpoints: webmail `/api/personal/calendar[*]` y `/api/personal/contacts[*]` (aislados por mailboxId);
  admin `/api/admin/domain/{id}/groups[*]` y `/api/admin/groups/expand/...`.
- Migración EF `CalendarContactsGroups`.

## Alta disponibilidad y observabilidad (spec §32, §37-38) — FASE 7
- **Worker concurrente**: `DeliveryWorker` procesa hasta `Delivery:Concurrency` (default 4) items en
  paralelo, cada uno con su scope y lease/claim atómico sobre MySQL (`ClaimNextAsync`). Una excepción en
  un item no aborta el lote (`SafeProcessAsync`). Crash recovery: lease caducado → el item vuelve a
  procesable (sin pérdida ni doble entrega controlada).
- **Métricas (`IMetricsRegistry`, §32)**: contadores + gauges + timers thread-safe, SIN datos sensibles
  (no direcciones/asuntos/contenido). Registradas en ingesta (received/spam/quarantined/malware),
  entrega (delivered/deferred/failed, delivery_timing), login (attempts/failures/successes) y worker
  (queue.pending, storage.bytes, concurrency, last_heartbeat_unix).
- **Endpoints**: `GET /api/metrics` (JSON con backfill de storage/queue) y `GET /api/metrics/text`
  (texto plano Prometheus, nombres sanitizados). `/health` y `/api/health` existentes.

## IA opcional y desacoplada (spec §31) — FASE 8
- `IMailIntelligenceService` (Application) desacoplado; servidor funciona sin IA (Disabled por defecto).
- `Security/MailIntelligence/LocalMailIntelligenceService`: 100% local/heurística — prioridad (0-100),
  clasificación (General/Urgent/Finance/Marketing/Newsletter/Social/Notification/Security), phishing
  asistido (0-100 + señales, URLs IP/redirectors) y resumen extractivo. **No envía contenido a
  proveedores externos** (§31). Es asistencia, nunca decisión final de seguridad.
- `IMailIntelligenceBackend` (Application) + `Infrastructure/Ai/OpenAiCompatibleMailIntelligenceBackend`:
  LLM remoto OpenAI-compatible para funciones avanzadas (traducir, respuesta sugerida, borrador,
  clasificación LLM, búsqueda semántica con fallback coseno local).
- **Doble consentimiento §31**: el backend nunca se usa sin `Ai:Backend:Enabled` (config explícita) Y
  `Mailbox.AiConsent` (falso por defecto, vía `POST /api/personal/ai/consent`). `MailIntelligenceFacade`
  garantiza este gate antes de enviar contenido a un proveedor.
- Activación IA: `Ai__Enabled=true` (local) y `Ai__Backend__Enabled=true` + `Endpoint`/`ApiKey`/`Model` (remoto).
- Migración EF `AiConsent`.

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