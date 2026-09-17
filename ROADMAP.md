# AtlasMail — ROADMAP

Estado: **Ciclo 1 completado** (vertical slice funcional, §52 del spec maestro 1.md),
**FASE 2 completada** (SMTP robusto + entrega externa),
**FASE 3 completada** (IMAP y clientes externos),
**FASE 4 completada** (SPF/DKIM/DMARC) y
**FASE 5 completada** (antispam/quarantine/antimalware).

## CICLO 1 — COMPLETADO ✅
- Arquitectura modular (`src/*` + `tests/*`, .NET 8, MySQL/Pomelo).
- Dominio/buzones/alias/usuarios, roles, multi-dominio, plus addressing, catch-all (disabled default).
- SMTP local real + webmail + cola (lease/claim + retry/backoff) + auditoría + message trace + backup/restore.
- Deny-by-default, antiforgery, IDOR controlado, PBKDF2, relay policy.
- Build 0/0; unit 56 + integration 13; SMTP E2E real y relay denied probados.

## FASE 2 — SMTP robusto + entrega externa ✅
- **Entrega externa real**: `IMxResolver` (DnsClient) resuelve MX con fallback a registro A (RFC 5321 §5.1);
  `ExternalDeliveryService` resuelve MX → `SmtpClient` → clasifica 4xx (reintenta) vs 5xx (fallo/bounce).
  Multi-MX con failover; timeouts estrictos. Smoke real: `gmail.com → 5 MX OK` (health probe).
- **STARTTLS y AUTH (PLAIN/LOGIN) operativos**: cliente SMTP con STARTTLS oportunista/obligatorio y
  AUTH PLAIN; servidor SMTP autentica contra buzones locales (PBKDF2). Política de envío autenticado
  (`security.allowAuthenticatedExternalSend`). Corregido bug EHLO que impedía anunciar extensiones a
  clientes reales (`EHLO hostname` → 250 con extensiones).
- **Bounces/DSN seguros sin backscatter**: fallo permanente 5xx → `SmtpClient` clasifica; el worker
  genera DSN solo si el remitente es un buzón local válido (nunca rebota null-sender, anti-bucle).
- **Límites**: tamaño máx DATA, nº destinatarios, timeouts, rate limits por IP/usuario/dominio
  (`SlidingWindowRateLimiter`, configurables `Delivery:RateLimitPerMinute` / `PerDomainPerMinute`).
- **Observabilidad**: health DNS (`DnsHealthProbe` resuelve MX periódicamente) y métricas de cola
  (pending/processing/deferred/delivered/failed/dead-letter) en `/api/health`.
- Tests: **unit 64/64** (+9) e **integration 13/13** (+2): SMTP AUTH PLAIN real (235/535) y entrega
  externa E2E real por SMTP a un servidor remoto local (sin falsificar red).

## FASE 3 — IMAP y clientes externos ✅
- **Servidor IMAP4rev1** (`AtlasMail.Protocols.Imap.ImapServer`) incremental, desacoplado del
  almacenamiento vía `IMailboxBackend` (Application) + `MySqlMailboxBackend` (Infrastructure).
- **Primera meta funcional (§10) funcional y probada**: LOGIN/AUTH, LIST/LSUB carpetas, SELECT/EXAMINE,
  STATUS, FETCH (flags, UID, RFC822.SIZE, INTERNALDATE, BODY[]/RFC822.HEADER/BODY[TEXT]), STORE
  (+\Seen, \Flagged, \Deleted, SILENT, replace), SEARCH básico, MOVE, UID (FETCH/STORE/SEARCH/MOVE),
  EXPUNGE, CLOSE, APPEND (no-persistente, documentado).
- Carpetas estándar mapeadas a flags especiales: `\Sent \Drafts \Trash \Junk(Spam) \Archive`.
- `IsDeleted` (flag \Deleted) agregado al modelo con migración `ImapDeletedFlag`.
- **Interoperabilidad**: corregido BOM UTF-8 del saludo que rompería clientes reales; número de secuencia
  y UID correctos; `MOVE` emite `EXPUNGE`; `SELECT` entrega UIDVALIDITY/UIDNEXT.
- Tests: **unit 73/73** (+9 backend IMAP: auth, list, select, flags, expunge, fetch, move, cross-tenant)
  e **integration 15/15** (+2: sesión IMAP E2E real por socket y login inválido rechazado).
- **NO se declara compatibilidad PROVEN con Thunderbird/Outlook/Apple Mail hasta prueba real (§44)**:
  la compatibilidad se probó contra nuestra implementación real (cliente IMAP real por socket),
  no contra esos clientes todavía.

## FASE 4 — Autenticación de correo (SPF/DKIM/DMARC) ✅
- **SPF (RFC 7208)**: evaluador puro (`AtlasMail.Security.EmailAuth.SpfEvaluator`) con mecanismos
  ip4/ip6/a/mx/include/exists/all, qualifiers (+/-/~/?), `redirect`, macros %{d %{i} %{s} %{l} %{o} %{h}
  y límite de 10 consultas DNS. Resultados Pass/Fail/SoftFail/Neutral/None/TempError/PermError.
  Verificación en recepción (resolver DNS real) integrada al scoring y a la política; no se rechaza
  ciegamente SoftFail (§17).
- **DKIM (RFC 6376)**: firma en salida (RSA-SHA256, canonicalización relaxed/simple) en el worker
  si el dominio tiene la clave; verificación en recepción (extrae firmas, obtiene clave pública del
  selector `_atlasmail._domainkey` por DNS, verifica firma/body hash). Generación de clave y registro
  DNS administrable (§18). La clave privada jamás se expone por API.
- **DMARC (RFC 7489)**: evaluación con alineación SPF/DKIM (relaxed/strict, con registrable-domain
  para ccTLD), política configurable (none/quarantine/reject aplicadas según `Delivery:DmarcEnforce`).
  Resultados auditables en el audit del mensaje (`auth=[spf=.. dkim=.. dmarc=..]`).
- **Administración (§17-19)**: endpoints `/api/admin/domain/{id}/auth`, `auth/dkim/enable`,
  `auth/dmarc` — muestran los registros DNS a publicar (SPF, `selector._domainkey` TXT, `_dmarc` TXT).
- Tests: **unit 94/94** (+21: SPF parseo/resultados, DKIM roundtrip firmar/verificar/body-tampered,
  DMARC alignment/tld, DomainMailAuthService DKIM/DMARC, DkimOutboundSigner) e **integration 17/17**
  (+2: admin auth genera DKIM+DMARC, política inválida rechazada).
- Honestidad: la verificación SPF/DKIM/DMARC en recepción contra DNS público depende de que el dominio
  exista; en la suite se valida el flujo sin red (SPF None / DKIM none / DMARC NoRecord).

## FASE 5 — Antispam / quarantine / antimalware ✅
- **Antimalware heurístico real** (`HeuristicAttachmentScanner`, spec §21): NO es NoOp. Detecta por
  extensión de alto riesgo (executables/scripts), double-extension/spoofing, magic bytes (MZ/ELF/Mach-O/PDF),
  macros VBA en `.docm/.xlsm/.pptm` (ZIP con vbaProject.bin) y HTML ofuscado. Devuelve
  Clean/Suspicious/Malicious/Unknown/ScannerUnavailable. **Nunca etiqueta Unknown como Clean** (§21).
  Interfaz `IAttachmentScanner` lista para pluguear ClamAV u otro motor.
- **Integración al pipeline**: en recepción se escanea cada adjunto (llena `Attachment.ScanStatus`),
  Malicious → cuarentena `malware`, Suspicious sube el score; se usa el score total
  (spam + auth + malware) para la decisión.
- **Cuarentena (§22)**: `Message.IsQuarantined` + motivo + fecha. `QuarantineService` permite a
  SecurityAdmin listar, inspeccionar (metadatos/adjuntos, sin MIME completo), **liberar**, **eliminar**
  y **bloquear remitente** (exacto o por dominio). La blocklist rechaza en la ingesta antes de persistir.
- **Endpoints (SecurityAdmin/SuperAdmin)**: `GET/POST/DELETE /api/admin/quarantine[...]`,
  `GET /api/admin/quarantine/blocked`, `POST /api/admin/quarantine/block`, `DELETE /api/admin/quarantine/blocked/{id}`.
- Migración EF `QuarantineBlockSenders` (BlockedSenders + campos de Message).
- Tests: **unit 105/105** (+11: scanner heurístico electrizado, doble-ext, macro VBA, Unknown≠Clean,
  zip con exe, quarantine liberar/eliminar/bloquear-domain) e **integration 18/18** (+1: endpoints de cuarentena).

## FASE 6 — Calendar / contacts / groups
- Calendario (día/semana/mes, `.ics`), contactos (VCARD/CSV), listas de distribución y grupos con políticas.

## FASE 7 — Alta disponibilidad / replicación / observabilidad avanzada
- Múltiples workers, reaprovechamiento de lease, replicación de message store, métricas/paneles.

## FASE 8 — IA opcional y desacoplada
- `IMailIntelligenceService` (Disabled por defecto): resumen, clasificación, prioridad, phishing asistido,
  búsqueda semántica. Sin IA el servidor funciona completo.

## Principios permanentes (§53)
NO open relay · NO pérdida silenciosa · NO doble entrega · NO passwords reversibles ·
NO cross-tenant access · NO secretos en logs · NO falsa evidencia. Fail-safe.