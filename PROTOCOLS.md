# AtlasMail — Protocolos

## SMTP (RFC 5321) — Ciclo 1 + FASE 2
Servidor SMTP real (`AtlasMail.Protocols.Smtp.SmtpServer`), state machine multihilo por conexión.

### Comandos soportados
| Comando | Comportamiento |
|---|---|
| EHLO / HELO | Saludo y anuncio de extensiones: SIZE, 8BITMIME, STARTTLS, AUTH PLAIN LOGIN, ENHANCEDSTATUSCODES, HELP |
| AUTH PLAIN / LOGIN | **Autentica contra buzones locales (PBKDF2)**; `user@dominio` o `user`. Éxito → 235; fallo → 535. Requiere EHLO previo |
| MAIL FROM | Abre el sobre; valida sintaxis del path |
| RCPT TO | Valida el sobre con **política de relay** (RCPT local válido o autenticado al externo → 250; else 550 relay denied) |
| DATA | Recibe el mensaje hasta `.<CR><LF>`; aplica dot-stuffing; invoca el pipeline de ingesta |
| RSET | Reinicia el sobre |
| NOOP | Keep-alive |
| QUIT | Cierra |

> **Corrección en FASE 2**: EHLO ahora acepta `EHLO hostname` (los clientes reales siempre lo envían con
> argumento). Antes el servidor solo respondía a `EHLO`/`HELO` exactos (bug de Ciclo 1), con lo que las
> extensiones no se anunciaban a clientes reales.

### Límites aplicados
- Tamaño máximo de DATA (`Smtp.MaxMessageBytes`, 50 MiB por defecto).
- Timeout de comando y por operación.
- Límite de conexiones por IP (anti-flooding).
- Línea máxima de comando (1000).

### Política de relay (no open relay)
- Conexión anónima (Internet): **sólo** puede entregar a dominios/buzones locales válidos.
  Destinatario inexistente local → 550. Dominio externo anónimo → **550 5.7.1 Relay access denied**.
- Autenticado autorizado (AUTH): puede enviar externamente conforme a política
  (`security.allowAuthenticatedExternalSend`, por defecto `true`).

### Códigos de respuesta (estado del sobre y mensaje)
- RCPT aceptada → `250 2.1.0 OK`; segura → el handler devuelve `550`/`501`/etc.
- DATA OK → `250 2.0.0 OK queued`; relay → `550 5.7.1`.
- Errores internos → `451 4.3.0 Temporary failure` (retryable).

### STARTTLS / AUTH
- Servidor anuncia `STARTTLS` (cuando se provee certificado) y `AUTH PLAIN LOGIN`. AUTH `PLAIN/LOGIN`
  **operativo** en FASE 2, validando password de buzón (nunca reversible; PBKDF2).
- Cliente (`SmtpClient`) admite STARTTLS **oportunista** (`StartTlsMode.Opportunistic`, por defecto),
  **obligatorio** (`Required`) o **desactivado** (`Disabled`, pruebas). AUTH PLAIN opcional si hay credenciales.

## Cliente SMTP (outbound) — `SmtpClient` (FASE 2 operativo)
- Resolución MX real (DnsClient) con fallback a registro A (RFC 5321 §5.1) en `DnsMxResolver`.
- `ExternalDeliveryService`: MX → SMTP, multi-MX con failover, timeouts estrictos, y clasificación
  temporario/permanente (4xx = retry, 5xx = failed/bounce).
- El worker encola correos externos y los entrega por este pipeline (antes: dead-letter FASE 2 pendiente).
- Fallos permanentes (5xx) → el worker genera **bounce/DSN seguro sin backscatter** (solo si el remitente
  es un buzón local válido; nunca un null-sender). Fail-safe: no hay pérdida silenciosa.
- Rate limits por usuario/dominio (`Delivery:RateLimitPerMinute`, `Delivery:RateLimitPerDomainPerMinute`).

## MIME (RFC 5322 + 2045-2047) — parser y builder
- Parseo: headers, `From/To/Cc/Bcc`, `Subject` (encoded-words UTF-8), fecha, `Message-ID`, `In-Reply-To`.
- Cuerpos: `text/plain`, `text/html`, `multipart/alternative`, `multipart/mixed`.
- Adjuntos: `Content-Disposition: attachment`, `Content-Type`/`name`, `Content-Transfer-Encoding`
  (base64, quoted-printable), nombre saneado, SHA-256 por adjunto.
- Preserva el mensaje original; nunca ejecuta contenido.
- Builder (`MimeBuilder`) genera mensajes válidos (headers escapados, adjuntos en base64).

## Verificación de protocolo
- **Unit**: parser MIME, reglas de relay, cuota, retry, rate limiter, entrega externa (MX→clasificación).
- **Integración SMTP E2E real** (`SmtpE2ETests`): servidor SMTP real contra MySQL temporal; conduce con
  sockets SMTP reales → RCPT 250/250 OK local; RCPT externo → 550 relay denied; **AUTH PLAIN → 235/535 real**.
- **Integración entrega externa E2E** (`ExternalDeliveryE2ETests`): SmtpClient real → servidor remoto local
  simulando un MX externo (EHLO/MAIL/RCPT/DATA reales, sin falsificar red).
- **Smoke manual** (cliente SMTP real + MySQL vivo): EHLO 250 con extensiones, AUTH PLAIN 235,
  entrega local → Inbox, health `smtp:true`, `dns:true` (MX gmail.com → 5).

## IMAP (RFC 3501) — FASE 3
`AtlasMail.Protocols.Imap.ImapServer` + `ImapSession` (state machine multihilo por conexión). NOT AUTH → AUTH → SELECTED.
Desacoplado del almacenamiento vía `IMailboxBackend` (`MySqlMailboxBackend` sobre metadatos MySQL + `IMessageStore`).

### Comandos soportados (primera meta funcional, §10)
| Comando | Comportamiento |
|---|---|
| CAPABILITY / NOOP | Annuncia IMAP4rev1; keep-alive |
| LOGIN | Autentica contra buzón local (PBKDF2 con password del buzón); fallo → `NO` |
| LIST / LSUB | Carpetas con flags especiales: `\Sent \Drafts \Trash \Junk(Spam) \Archive`, delimitador `/` |
| SELECT / EXAMINE | Entrega `N EXISTS`, `N RECENT`, `UIDVALIDITY`, `UIDNEXT`, `FLAGS (\Seen \Flagged \Deleted)` |
| STATUS | MESSAGES / UIDNEXT / UIDVALIDITY / UNSEEN |
| FETCH | flags, UID, RFC822.SIZE, INTERNALDATE, BODY[] / RFC822.HEADER / BODY[TEXT] (literales `{N}`) |
| STORE | `+FLAGS` / `-FLAGS` / `FLAGS` (replace) con `\Seen \Flagged \Deleted` y `SILENT` |
| SEARCH | básico: ALL, UNSEEN, SEEN, SUBJECT, FROM |
| MOVE | mueve y emite `EXPUNGE` por mensaje (RFC 6851) |
| UID | FETCH / STORE / SEARCH / MOVE por UID estable y único por carpeta |
| EXPUNGE | elimina permanentemente los `\Deleted` |
| CLOSE | expunge implícito y deselecciona |
| APPEND | aceptado pero **no persistente** (documentado como no-PROVEN en esta fase) |
| LOGOUT | `* BYE` + cierre |

### Notas de compatibilidad
- UTF-8 sin BOM en el saludo (un BOM rompe la negociación con clientes reales).
- `\Deleted` se persiste en `Message.IsDeleted` (migración `ImapDeletedFlag`).
- **Compatible con Thunderbird/Outlook/Apple Mail: NO PROVEN aún (§44)** — se probó contra un cliente
  IMAP real por socket (nuestra implementación), no contra esos clientes. Prueba real pendiente.

## Autenticación de correo (spec §17-19) — FASE 4
Evaluación en recepción y firma en salida. Resultados auditables vía metadata del mensaje (`auth=[...]`).

### SPF (RFC 7208)
- `SpfEvaluator`: mecanismos ip4/ip6/a/mx/include/exists/all; qualifiers `+ - ~ ?`; `redirect`;
  macros `%{d %{i} %{s} %{l} %{o} %{h}`. Límite 10 consultas DNS.
- Resultados: Pass / Fail / SoftFail / Neutral / None / TempError / PermError.
- Verificación en recepción contra la IP del remitente; se integra al scoring sin rechazar SoftFail ciegamente.
- Registro a publicar: `v=spf1 mx ~all` (el admin añade sus IPs).

### DKIM (RFC 6376)
- Firma en salida (RSA-SHA256, canonicalización relaxed/simple) en el worker si el dominio tiene la clave.
- Verificación en recepción: extrae `DKIM-Signature`, obtiene clave pública de
  `<selector>._domainkey.<dominio>`, verifica firma + body hash.
- Selector por defecto `atlasmail`. Clave privada almacenada, jamás expuesta por API.
- Registro a publicar: `v=DKIM1; k=rsa; p=<clave-pública>` en `atlasmail._domainkey.<dominio>` TXT.

### DMARC (RFC 7489)
- Evaluación con alineación SPF/DKIM (relaxed/strict; registrable-domain para ccTLD).
- Política configurable por dominio: none / quarantine / reject (`Delivery:DmarcEnforce` controla si se aplica).
- Registro a publicar: `_dmarc.<dominio>` TXT: `v=DMARC1; p=<policy>; adkim=r; aspf=r; fo=1`.

### Endpoints admin (SuperAdmin)
- `GET /api/admin/domain/{id}/auth` — estado y registros DNS a publicar.
- `POST /api/admin/domain/{id}/auth/dkim/enable` — genera clave DKIM + registro TXT del selector.
- `POST /api/admin/domain/{id}/auth/dmarc` — body: `"none"|"quarantine"|"reject"`.

## Calendario / contactos / grupos (spec §14-16) — FASE 6
Colaboración por buzón/webmail, con interoperabilidad estándar.

### Calendario (§15) — RFC 5545
- Eventos: title, description, location, start/end (UTC), timezone IANA, organizer, attendees (PARTSTAT),
  status (confirmed/tentative/cancelled), recurrencia RRULE, all-day, ExternalUid (import).
- Export/import `.ics`: `BEGIN:VCALENDAR` con `VEVENT`, fold de líneas, escape de `;,`: \\
  Import idempotente por UID. No se afirma compatibilidad Exchange/Outlook completa (§15).
- Webmail: `GET/POST/PUT/DELETE /api/personal/calendar[...]`,
  `GET /api/personal/calendar/export.ics`, `POST /api/personal/calendar/import.ics`.

### Contactos (§14) — VCARD (RFC 6350) y CSV
- Personal (OwnerMailboxId) o de dominio (DomainId); listas personales.
- `VCARD` (FN/EMAIL/TEL/ORG/NOTE) y `CSV` (Name,Email,Phone,Company,Notes).
- Webmail: `GET/POST/PUT/DELETE /api/personal/contacts[...]`,
  `GET /api/personal/contacts/export.vcf|.csv`, `POST /api/personal/contacts/import.vcf|.csv`.

### Grupos / listas de distribución (§16)
- `DistributionList` + `DistributionListMember`: `ventas@empresa.mx → ana@, juan@, maria@`.
- Políticas: InternalOnly (solo buzones locales) / ExternalAllowed, moderación opcional, MaxMembers.
- Expansión anti-loop: profundidad máx 8, detección de ciclos y dedupe.
- Admin: `GET/POST/PUT/DELETE /api/admin/domain/{id}/groups[...]`,
  `GET /api/admin/groups/expand/{localPart}/{domain}`.
- SMTP: la lista local se acepta como destinatario y en la ingesta se expande a cada miembro local (audit `Smtp.List`).

## IMAP / Webmail / Búsqueda
- IMAP: FASE 3 (arriba).
- Webmail: RFC-compatible a nivel de metadatos vía API interna (no protocolo wire).
- Búsqueda `IMessageSearchService`: filtro por sender/recipient/subject/body/date/attachment, aislado por buzón.