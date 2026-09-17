# AtlasMail — Evidencia de prueba (Ciclo 1 + FASE 2 a FASE 8 + backend de IA)

Fecha FASE 8-avanzada: 2026-09-17. Entorno: Windows, MySQL 8.0.46 local, .NET 8.0.425.

## FASE 8 (avanzada) — Backend de IA — Definition of Done

| Requisito (spec §31) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **129/129** + Integration **20/20** = **149/149** |
| `IMailIntelligenceBackend` desacoplado | ✅ | Application; servidor funciona sin backend (Disabled default) |
| LLM OpenAI-compatible | ✅ | `OpenAiCompatibleMailIntelligenceBackend` vía /v1/chat/completions |
| Funciones avanzadas | ✅ | traducir, respuesta sugerida, borrador, clasificación LLM, búsqueda semántica |
| Fallback local | ✅ | backend caído/endpoint falso → fallback (no rompe flujo); coseno local en semántica |
| **Doble consentimiento §31** | ✅ | Backend solo si `Ai:Backend:Enabled` Y `Mailbox.AiConsent` (falso default) |
| No enviar contenido sin consentimiento | ✅ | `MailIntelligenceFacade` gate; test `Consentimiento_por_defecto_falso` |
| Activación explícita | ✅ | `Ai__Backend__Enabled` + Endpoint/ApiKey/Model |
| Migración EF | ✅ | `AiConsent` (Mailbox.AiConsent) |

## Ejecuciones reales FASE 8-avanzada

### Build / Tests
```
Compilación correcta.  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  129/129 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  20/20 (0 error)
```

### Tests relevantes (unit)
```
AiBackend: parsea respuesta OpenAI-compatible ("Hola") · endpoint falla → fallback local ·
           semántica usa coseno local · Disabled no activo · consentimiento por defecto falso/revocable
```

### Nota de honestidad (FASE 8-avanzada)
- El backend se probó con fake HttpMessageHandler (sin red). La integración con un LLM real requiere
  `Ai:Backend:Endpoint`/`ApiKey` y queda como EXTERNAL (no se declara PROVEN sin un endpoint real).
- El gate de consentimiento es por diseño: ningún contenido se envía a un proveedor sin config explícita
  del operador + consentimiento del buzón (§31).

## FASE 8 — IA local — Definition of Done (para referencia)

| Requisito IA local (spec §31) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **124/124** + Integration **20/20** = **144/144** |
| `IMailIntelligenceService` desacoplado | ✅ | Application; servidor funciona sin IA (Disabled default) |
| Servidor sin IA | ✅ | Integration: `/api/personal/ai/analyze` responde `enabled:false` |
| IA local sin API comercial | ✅ | `LocalMailIntelligenceService` heurístico, determinista |
| No enviar contenido fuera (§31) | ✅ | 100% local; sin llamadas de red |
| Prioridad / clasificación | ✅ | Tests: Finance/Urgent, prioridad alta>baja |
| Detección phishing asistida | ✅ | Señales detectadas; phishing 0 en mensaje limpio |
| Resumen extractivo | ✅ | Acortado dentro de límite |
| Asistida, no decisión final | ✅ | Documentado; no afecta spam/antimalware |
| Activación explícita | ✅ | `Ai__Enabled`; endpoint webmail `POST ai/analyze` |

## Ejecuciones reales FASE 8

### Build / Tests
```
Compilación correcta.  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  124/124 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  20/20 (0 error)
```

### Smoke real (Ai__Enabled=true, MySQL vivo)
```
POST /api/personal/ai/analyze
  {subject:"URGENTE verifica tu cuenta", body:"Tu cuenta será suspendida. Click here to reset your password immediately."}
  -> enabled:true priority:58 category:Security phishing:50 signals:["click here to reset","tu cuenta será suspendida","urgencia: immediately"]
POST /api/personal/ai/analyze {subject:"Reunion", body:"Gracias por la nota, nos vemos el lunes."}
  -> priority:25 category:General phishing:0
```

### Nota de honestidad (FASE 8)
- IA es **local/heurística** (prioridad/clasificación/phishing asistido/resumen extractivo deterministas).
  No hay LLM externo: las funciones avanzadas del spec §31 (traducción, respuesta sugerida, búsqueda
  semántica) requieren un backend externo que, según §31, sólo se integraría con consentimiento explícito
  del operador. Hasta entonces se mantiene Disabled por defecto y sin envío de datos.
- La detección de phishing es ASISTIDA: puede tener falsos positivos/negativos y nunca bloquea por sí sola.

## FASE 7 — Definition of Done (para referencia)

| Requisito FASE 7 (spec §32, §37-38) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencias / 0 Errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **119/119** + Integration **19/19** = **138/138** |
| Worker concurrente (HA) | ✅ | `Delivery:Concurrency` default 4; `Task.WhenAll` de claims; `SafeProcessAsync` |
| Lease/claim transaccional + crash recovery | ✅ | Existing `ClaimNextAsync` atómico; lease caducado → reprocesable |
| `IMetricsRegistry` (contadores/gauges/timers) | ✅ | Thread-safe; tests increment/gauge/timer/paralelo |
| Métricas sin datos sensibles (§32) | ✅ | `metrics/text`: no contiene `password`/`secret` (test) |
| Métricas de login, ingesta, entrega, storage | ✅ | `auth.*`, `mail.*`, `delivery_timing`, `storage.bytes` |
| `GET /api/metrics` (JSON) | ✅ | Snapshot con login failures/successes, storage, queue, worker |
| `GET /api/metrics/text` (Prometheus, sanitizado) | ✅ | Nombres `_` (sanitizados); sin secrets |
| Heartbeat del worker | ✅ | `worker.last_heartbeat_unix` en snapshot |

## Ejecuciones reales FASE 7

### Build / Tests
```
Compilación correcta.  0 Advertencia(s)  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  119/119 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  19/19 (0 error)
```

### Tests relevantes (unit)
```
MetricsRegistry: acumula · gauge+timer (count/suma_ms) · texto sanitiza nombres (_) · thread-safe 1000 parallel
```

### Smoke real (login + metrics, MySQL vivo)
```
login ok  -> {username:"admin", role:"SuperAdmin"}
login bad -> {error:"Credenciales inválidas"}
/api/metrics keys: auth.login_attempts, auth.login_failures(1), auth.login_successes(1),
                   storage.bytes(0), queue.pending_total(0), worker.concurrency(4),
                   worker.last_heartbeat_unix(set)
/api/metrics/text: contiene "auth_login_failures "; NO contiene "password" ni "secret"
```

### Nota de honestidad (FASE 7)
- La replicación física del message store (multi-nodo) queda declarada como integrar en un despliegue
  multi-nodo; en esta fase la HA es de **workers concurrentes sobre la misma BD** + lease/claim atómico
  (ya no hay pérdida ni doble entrega por carrera ni crash mientras la cola y el store sean compartidos).
- Las métricas son agregados sin datos sensibles (por diseño §32); no se expone contenido ni direcciones.

## FASE 6 — Definition of Done (para referencia)

| Requisito FASE 6 (spec §14-16) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencias / 0 Errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **115/115** + Integration **18/18** = **133/133** |
| Calendario (eventos + attendees + RRULE + all-day) | ✅ | `CalendarService` CRUD; tests create/update/delete/rango |
| Export/import `.ics` (RFC 5545) | ✅ | Roundtrip: export→import; idempotente por UID; DTSTART/SUMMARY correctos |
| No afirmar Exchange/Outlook completa | ✅ | Documentado §15; sólo interoperabilidad `.ics` básica |
| Contactos personales/dominio + listas | ✅ | `ContactService` con OwnerMailboxId/DomainId |
| Import/export VCARD (RFC 6350) / CSV | ✅ | Roundtrip VCARD; CSV con campos entre comas |
| Listas de distribución (ventas@ → ana,juan) | ✅ | `GroupService` creada y expandida a miembros |
| Políticas de lista (envío, moderación, límite) | ✅ | `SendPolicy`/`ModerationEnabled`/`MaxMembers` |
| Prevención de loops en expansión | ✅ | Test con ciclo ventas↔devs: termina, sin duplicación |
| Integración SMTP de listas locales | ✅ | RCPT acepta lista; ingesta expande a buzones locales (audit `Smtp.List`) |
| Migración EF | ✅ | `CalendarContactsGroups` (Calendars, CalendarEvents, Attendees, DistributionLists, Members, Contact.OwnerMailboxId) |

## Ejecuciones reales FASE 6

### Build / Tests
```
Compilación correcta.  0 Advertencia(s)  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  115/115 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  18/18 (0 error)
```

### Tests relevantes (unit)
```
Calendario: create/list/delete · .ics export contiene BEGIN:VCALENDAR/SUMMARY/DTSTART:20261001T090000Z ·
            import idempotente por UID · filtro por rango de fechas
Contactos: CRUD personal · VCARD export/import roundtrip · CSV con campo comilla/comas
Grupos:    crear+expandir a miembros · loop ventas↔devs se previene (≤3, sin duplicados) ·
           dirección en uso por buzón se rechaza · política external+moderación+límite
```

### Smoke real (admin, MySQL vivo)
```
POST /api/admin/domain corp6.mx                    -> {id:1}
POST /domain/1/groups {name:"Ventas",localPart:"ventas",...} -> {email:"ventas@corp6.mx",...}
POST /domain/1/groups/{id}/members {address:"ana@corp6.mx"}
POST /domain/1/groups/{id}/members {address:"juan@corp6.mx"}
GET  /api/admin/groups/expand/ventas/corp6.mx      -> ["ana@corp6.mx","juan@corp6.mx"]
GET  /domain/1/groups                              -> [ventas] (1)
```

### Nota de honestidad (FASE 6)
- `CalendarService` Export/import maneja `VEVENT` básico (no se afirman capacidades Exchange/Outlook
  completas §15); recurrence se almacena como RRULE y se exporta tal cual (no se calculan ocurrencias).
- La entrega SMTP de una lista expande a miembros con **buzón local** (copias en la ingesta);
  los miembros sin buzón local se cuentan como "skipped" en el audit (acorde a servidor local; envío
  externo desde una lista es EXTERNAL para una fase posterior).

## FASE 5 — Definition of Done (para referencia)

| Requisito FASE 5 (spec §20-22) | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencias / 0 Errores` |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **105/105** + Integration **18/18** = **123/123** |
| Antimalware real (no NoOp) | ✅ | `HeuristicAttachmentScanner`: executable/vbs/doble-ext/VBA-macro/HTML-ofuscado/zip-exe |
| Nunca Unknown como Clean | ✅ | Unit `Binario_desconocido_es_Unknown_no_Clean` |
| Canales de antimalware (Clean/Suspicious/Malicious/Unknown/ScannerUnavailable) | ✅ | Enums + scanner con magic bytes y heurística local |
| Integración al pipeline de ingesta | ✅ | `Attachment.ScanStatus` poblado; Malicious→quarantine; score spam+auth+malware |
| Cuarentena: listar/inspeccionar | ✅ | `QuarantineService.List/Inspect` (metadatos sin MIME completo) |
| Cuarentena: liberar | ✅ | `ReleaseAsync` → carpeta destino, `SpamDecision` recalculado |
| Cuarentena: eliminar | ✅ | `DeleteAsync` borra metadata + blob del store |
| Bloquear remitente | ✅ | `BlockSenderAsync` exacto/dominio; mata cuarentena del remitente; rechaza en ingesta |
| Endpoints admin (SecurityAdmin/SuperAdmin) | ✅ | `/api/admin/quarantine[...]`, `/block`, `/blocked`, unblock |
| Migración EF | ✅ | `QuarantineBlockSenders` (tabla BlockedSenders + campos Message) |

## Ejecuciones reales FASE 5

### Build / Tests
```
Compilación correcta.  0 Advertencia(s)  0 Errores
AtlasMail.UnitTests.dll:  Correctas!  105/105 (0 error)
AtlasMail.IntegrationTests.dll: Correctas!  18/18 (0 error)
```

### Tests relevantes (unit)
```
Antimalware: .exe→Malicious · .vbs→Malicious · factura.pdf.exe→Suspicious · .docm(vbaProject.bin)→Malicious ·
             pdf/PNG reales→Clean · blob desconocido→Unknown (≠Clean) · zip(exe)→Suspicious · html ofuscado→Suspicious
Quarantine: bloquear dominio mata cuarentena del remitente y matcha en ingesta · unblock ·
            liberar (IsQuarantined=false, SpamDecision Allowed) · eliminar (borna metadata+blob)
```

### Smoke / API
```
GET  /api/admin/quarantine                    -> []
POST /api/admin/quarantine/block {value:"spammer@baddie.net",kind:"exact"} -> {blocked:true}
GET  /api/admin/quarantine/blocked            -> [{value:"spammer@baddie.net",...}]
DELETE /api/admin/quarantine/blocked/{id}     -> {unblocked:true}
GET  /api/admin/quarantine/blocked            -> []
```

### Nota de honestidad (FASE 5)
El scanner heurístico local (sin firma ni API comercial) es intencionadamente conservador: lo no
reconocido queda en **Unknown**, nunca Clean (§21). Un motor con firmas (ClamAV) puede pluguearse detrás
de `IAttachmentScanner`; hasta entonces Unknown/ScannerUnavailable se tratan con riesgo por defecto. La
exclusión de falsos positivos de determinación de malware por heurística es conservadora (no pierde correo:
lo que marca Malicious primero se cuarentena, no se pierde).

## FASE 4 — Definition of Done (para referencia)

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