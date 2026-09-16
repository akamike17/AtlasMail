# AtlasMail — Evidencia de prueba (Ciclo 1 + FASE 2 + FASE 3 + FASE 4)

Fecha FASE 4: 2026-09-16. Entorno: Windows, MySQL 8.0.46 local, .NET 8.0.425.

## FASE 4 — Definition of Done

| Requisito FASE 4 (spec §17-19) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencias / 0 Errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **94/94** + Integration **17/17** = **111/111** |
| SPF verificación (RFC 7208) | ✅ | `SpfEvaluator` unit: ip4/a/include/redirect/macro/SoftFail/Neutral/None/Fail/PermError |
| DKIM firma + verificación (RFC 6376) | ✅ | Roundtrip `Dkim.Sign`→`Verify` pasa; body-tampered NO pasa; canonicalización relaxed/simple |
| DKIM firma en salida (worker) | ✅ | `DkimOutboundSigner`: firma si el dominio tiene clave; sin clave → intacto |
| DMARC evaluación (RFC 7489) | ✅ | `DmarcEvaluator`: alignment relaxed/strict, registrable-domain ccTLD, p=none/reject |
| Política DMARC aplicada | ✅ | `EmailAuthenticationService` → `ShouldReject`/`ShouldQuarantine`; integrada a la ingesta |
| Admin DNS por dominio | ✅ | `DomainMailAuthService` + endpoints: SPF/DKIM/DMARC records a publicar |
| Clave privada no expuesta | ✅ | API devuelve `dkimPrivateKeyHint`, no la clave |
| Audit de auth | ✅ | metadata de mensaje: `auth=[spf=.. dkim=.. dmarc=..]` |

## Ejecuciones reales FASE 4

### Build / Tests
```
Compilación correcta.  0 Advertencia(s)  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  94/94 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  17/17 (0 error)
```

### Tests relevantes (unit)
```
SPF: Pass ip4 · Fail ip no autorizada (-all) · SoftFail ~all · Neutral ?all · None sin registro ·
     Pass a/ · Pass include · Pass redirect · macro %{i} · PermError sin all
DKIM: roundtrip Pass (256/1024 RSA) · body-tampered !=  Pass · canonicalización relaxed/simple
DMARC: p=none Pass no reject · p=reject Fail → reject · NoRecord sin registro
DomainMailAuth: enable DKIM → selector + registro TXT v=DKIM1;k=rsa;p= ; set DMARC reject / inválido rechazado
DkimOutboundSigner: firma si dominio habilitado; intacto si no
```

### Smoke admin (integration)
```
POST /api/admin/domain authadmin.local                 -> {"id":..}
GET  /api/admin/domain/{id}/auth                       -> dkimEnabled:false, spfRecordToPublish:"v=spf1 mx ~all"
POST /api/admin/domain/{id}/auth/dkim/enable           -> dkimEnabled:true, dkimSelector:"atlasmail",
                                                           dkimPublicKeyRecord:"v=DKIM1; k=rsa; p=...",
                                                           dkimPrivateKeyHint:"clave privada almacenada (no expuesta)"
POST /api/admin/domain/{id}/auth/dmarc "reject"        -> dmarcPolicy:"reject", dmarcRecordToPublish:"v=DMARC1; p=reject; ..."
POST /api/admin/domain/{id}/auth/dmarc "bogus"         -> 400
```

### Nota de honestidad (FASE 4)
La verificación SPF/DKIM/DMARC en recepción consulta DNS público; en la suite sin red se valida el flujo
con dominios inexistentes (SPF None / DKIM none / DMARC NoRecord). La firma/verificación DKIM real
se valida por roundtrip criptográfico (unit). La compatibilidad de autenticación de correo contra
proveedores reales requiere DNS público y reputación IP (EXTERNAL), no se declara PROVEN sin prueba real.

## FASE 3 — evidencia (para referencia)
Unit 73/73 + Integration 15/15 (IMAP E2E real; login inválido).

| Requisito FASE 3 (spec §10) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencias / 0 Errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **73/73** + Integration **15/15** = **88/88** |
| IMAP server real | ✅ | `ImapServer` + `ImapSession` (RFC 3501) desacoplado vía `IMailboxBackend` |
| LOGIN/autenticación | ✅ | `MySqlMailboxBackendTests.Autenticacion_valida_y_rechaza`; E2E `X1 OK` / `NO` |
| Listar carpetas | ✅ | E2E `LIST` → 6 carpetas con flags `\Sent \Drafts \Trash \Junk(Spam) \Archive` |
| Seleccionar carpeta | ✅ | `SELECT Inbox` → `2 EXISTS` + `UIDVALIDITY` |
| Listar mensajes | ✅ | `FETCH 1:2 (FLAGS UID RFC822.SIZE INTERNALDATE)` |
| Obtener mensaje | ✅ | `FETCH 1 (BODY[])` → literal `{N}` con MIME completo (`Subject: Correo 1`) |
| Flags / marcar leído | ✅ | `STORE +FLAGS.SILENT \Seen` y `STORE +FLAGS \Flagged` → respuesta con estado |
| Marcar deleted + eliminar | ✅ | `UID STORE \Deleted` + `EXPUNGE` |
| Mover | ✅ | `MOVE 2 Trash` → `* 2 EXPUNGE`; `SELECT Trash` → `1 EXISTS` |
| Migración `\Deleted` | ✅ | `ImapDeletedFlag` (`Message.IsDeleted`) |
| Interop clientes externos | ⏳ | NO PROVEN (§44): probado contra cliente IMAP real propio, no contra Thunderbird/Outlook/Apple Mail |

## Ejecuciones reales FASE 3 (salida de herramientas)

### Build / Tests
```
Compilación correcta.  0 Advertencia(s)  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  73/73 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  15/15 (0 error)
```

### Sesión IMAP E2E real (servidor en ejecución, MySQL vivo, puerto 1143)
```
GET /api/health                  -> {"status":"ok","components":{"smtp":true,"imap":true},...}
GREET                            -> * OK [atlasmail.local] AtlasMail IMAP4rev1 ready   (sin BOM)
LOGIN carlos@imap3.local         -> X1 OK LOGIN completed
LIST "" "*"                      -> 6 carpetas: Inbox \Sent \Drafts \Trash \Junk \Archive
SELECT Inbox                     -> * 2 EXISTS ; * OK [UIDVALIDITY 2000000001]
FETCH 1:2 (...)                  -> UID 1 y UID 3 con FLAGS / RFC822.SIZE / INTERNALDATE
FETCH 1 (BODY[])                 -> literal {264} con "Subject: Correo 1" + cuerpo
STORE 1 +FLAGS.SILENT \Seen      -> X6 OK STORE completed
STORE 2 +FLAGS \Flagged          -> * 2 FETCH (FLAGS (\Flagged) UID 3)
UID STORE 1 +FLAGS \Deleted      -> X8 OK STORE completed
MOVE 2 Trash                     -> * 2 EXPUNGE ; X9 OK MOVE completed
EXPUNGE                          -> X10 OK EXPUNGE completed
SELECT Trash                     -> * 1 EXISTS
LOGOUT                           -> * BYE ; X12 OK LOGOUT completed
```

## FASE 2 — evidencia (para referencia)
Unit 64/64 + Integration 13/13 (SMTP AUTH real 235/535; entrega externa E2E; DNS MX gmail.com→5).

| Requisito FASE 2 | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencias / 0 Errores` (toda la solución) |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **64/64** + Integration **13/13** = **77/77** |
| Entrega externa real MX | ✅ | `DnsMxResolver` (DnsClient); health probe real `gmail.com → 5 MX OK` |
| SMTP E2E entrega externa por protocolo | ✅ | `ExternalDeliveryE2ETests`: SmtpClient real → servidor "MX" local (EHLO/MAIL/RCPT/DATA) |
| SMTP AUTH PLAIN real | ✅ | `Smtp_AUTH_PLAIN_legitimo_y_login_desde_Tcp`: 235 correctas / 535 incorrectas |
| EHLO de clientes reales | ✅ | Corrección bug Ciclo 1: `EHLO hostname` → 250 con SIZE/8BITMIME/AUTH/ENHANCEDSTATUSCODES |
| Bounces/DSN sin backscatter | ✅ | Worker sólo rebota a remitente local válido; nunca null-sender |
| Rate limits por usuario/dominio | ✅ | `SlidingWindowRateLimiter` unit-tested; configurable por `Delivery:*` |
| Observabilidad | ✅ | `/api/health` → `{smtp,dns}` + métricas queue (pending/processing/deferred/delivered/failed/deadLetter) |

## Ejecuciones reales FASE 2 (salida de herramientas)

### Build
```
Compilación correcta.
0 Advertencia(s)
0 Errores
```

### Tests
```
AtlasMail.UnitTests.dll:  Correctas!  64/64 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  13/13 (0 error)
```

### Smoke E2E real contra MySQL local (servidor en ejecución, BD limpia)
```
POST /api/auth/login admin              -> {"username":"admin","role":"SuperAdmin"}
POST /api/admin/domain smoke.local      -> {"id":1}
POST /api/admin/mailbox alice           -> {"id":1}
GET  /api/health                        -> {"status":"ok","components":{"smtp":true,"dns":true},"queue":{...0s}}
SMTP real EHLO                          -> 250-SIZE 52428800 / 250-8BITMIME / 250-AUTH PLAIN LOGIN / 250-ENHANCEDSTATUSCODES
SMTP real AUTH PLAIN alice              -> 235 2.7.0 Authentication successful
SMTP real MAIL/RCPT/DATA alice@smoke.local -> 250 2.0.0 OK queued
Web alice (login) -> folders:6 -> INBOX 1 mensaje "Smoke F2" de carol@external.example
DNS health probe gmail.com              -> OK (5 MX)
```

## Ciclo 1 — evidencia (2026-09-15, para referencia)

### Definition of Done (spec §51)

| Requisito | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencia(s) / 0 Errores` (toda la solución) |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit 55/55 + Integration 11/11 = 66/66 |
| MySQL integration | ✅ | IntegrationTests usan BD **temporal** `atlasmail_ci_<guid>` (spec §35), creada/migrada/borrada |
| SMTP E2E local | ✅ | server SMTP real + socket real → `bob@atlas.local` 250 OK; llega a Inbox |
| Relay no autorizado | ✅ | SMTP externo `victim@gmail.com` → `550 5.7.1 Relay access denied` |
| Fresh install / second startup | ✅ | migración `InitialCreate` y arranque idempotente |
| Backup/restore temporal | ✅ | backup con manifest+SHA256; restore contra BD temporal |
| No secretos en repo | ✅ | conexión/admin vía entorno; `.gitignore` excluye store/backups |

## Ejecuciones reales (salida de herramientas)

### Build
```
ATLASMAIL> dotnet build AtlasMail.slnx -c Release
Compilación correcta.
0 Advertencia(s)
0 Errores
```

### Tests
```
AtlasMail.UnitTests.dll:  Correctas!  55/55 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  11/11 (0 error)
```

### Smoke E2E (Manual, servidor en ejecución contra MySQL real local)
```
GET /health                          -> 200
GET /Account/Login                   -> 200 HTML
POST /api/auth/login {admin}         -> {"username":"admin","role":"SuperAdmin"}
POST /api/admin/domain  atlas.local  -> {"id":1}
POST /api/admin/mailbox alice,bob    -> {"id":1},{"id":2}
POST /api/admin/user   alice,bob     -> {"id":2},{"id":3}
POST /api/mail/compose alice->bob    -> {"success":true,"storeKey":"...eml"}
GET  /api/mail/folder/{bobInbox}     -> mensaje "Segundo mensaje" de alice
GET  /api/mail/search?body=prueba    -> resultados (bob)
SMTP real: outsider@elsewhere.example -> bob@atlas.local   -> 250 2.0.0 OK  (llega a Inbox)
SMTP real: victim@gmail.com (anonimo)             -> 550 5.7.1 Relay access denied
POST /api/admin/backup               -> backupId + sha256 + 14 tablas
POST /api/admin/backup/{id}/restore  -> success:true (BD temporal)
GET  /api/admin/audit                -> Auth.* / Domain.Create / Mailbox.Create / Smtp.Ingest / Smtp.Reject...
GET  /api/admin/queue                -> item estado DeadLetter motivo fase2 (ya resuelto en FASE 2)
```

## Tests unitarios FASE 2 (+9 → 64) — áreas nuevas (spec §34 + FASE 2)
rate limiter (ventana deslizante por clave) · entrega externa → MX: éxito, 5xx permanente, 4xx temporal,
sin MX, política denegada, failover a siguiente MX.

## Tests de integración FASE 2 (+2 → 13) — casos nuevos (spec §35 + FASE 2)
**SMTP AUTH PLAIN real** (235 correcta / 535 incorrecta contra password de buzón) ·
**entrega externa E2E real por SMTP** a un servidor "MX" local (EHLO/MAIL/RCPT/DATA reales, sin falsificar red).

## Nota de honestidad
La **entrega externa** usa resolución MX real (DnsClient). La entrega PROVEN por protocolo (SmtpClient real
→ servidor SMTP remoto local, validado E2E) y la resolución MX real se verifican. No se declara compatibilidad
con proveedores/ISP concretos sin prueba; el health probe reporta MX real (gmail.com → 5). Sólo se declara
PROVEN lo que se probó.