# AtlasMail — Evidencia de prueba (Ciclo 1)

Fecha de ejecución: 2026-09-15. Entorno: Windows, MySQL 8.0.46 local, .NET 8.0.425.

## Definition of Done (spec §51)

| Requisito | Resultado | Evidencia |
|---|---|---|
| `dotnet build -c Release` → 0 errores | ✅ | `0 Advertencia(s) / 0 Errores` (toda la solución) |
| `dotnet test -c Release` → ALL PASS | ✅ | Unit **55/55** + Integration **11/11** = **66/66** |
| MySQL integration | ✅ | IntegrationTests usan BD **temporal** `atlasmail_ci_<guid>` (spec §35: no tocar DB real), creada/migrada/borrada por prueba |
| SMTP E2E local | ✅ | `SmtpE2ETests`: server SMTP real + socket real → `bob@atlas.local` RCPT 250 / DATA 250 OK; llega a Inbox |
| Relay no autorizado | ✅ | SMTP externo `victim@gmail.com` → `550 5.7.1 Relay access denied` |
| Web E2E | ✅ | login → crear dominio → buzones Alice/Bob → Alice redacta → Bob Inbox → lee/marca leído → busca |
| Fresh install | ✅ | migración `InitialCreate` aplicada de cero; BD creada automáticamente |
| Second startup | ✅ | arranque idempotente: "No migrations were applied. DB up to date", seed admin no duplica |
| Backup/restore temporal | ✅ | backup con manifest+SHA256; restore contra BD temporal conserva datos |
| No secretos en repo | ✅ | conexión/admin vía variables de entorno; `.gitignore` excluye mailstore/backups/*.user* |

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
GET  /api/admin/queue                -> item estado DeadLetter motivo fase2 (external delivery pendiente)
```

## Cobertura funcional del Ciclo 1 (spec §50)
- ADMIN crea dominio ✅ · crea Alice/Bob ✅ · Alice login web ✅ · Alice redacta ✅
- AtlasMail guarda/procesa ✅ · Bob Inbox ✅ · Bob lee/responde ✅
- SMTP externo local real entrega a Bob ✅ · relay no autorizado DENIED ✅
- Cola observable ✅ · message trace observable ✅ · backup creado ✅ · restore temporal probado ✅
- Audit registrado ✅ · build/tests PASS ✅

## Tests unitarios (55) — áreas (spec §34)
address parsing · relay rules · MIME · spam scoring · rule engine · quota · retry/backoff ·
password policy · password hasher · mailbox resolution (directo/alias/plus/catch-all) ·
queue lease/claim/reclaim · IDOR (buzón A no ve B).

## Tests de integración (11) — casos (spec §35)
deny-by-default, login+dashboard, crear dominio/buzones/alias/usuarios, alias→buzón, password débil
rechazada (2 casos), RBAC (rol no crea dominio), backup+manifest, restore preserva datos, restore
inexistente falla limpio, **SMTP E2E real local + relay denegado**.

## Nota de honestidad
La **entrega externa** (MX/DNS real) está marcada como FASE 2 y pendiente en el ROADMAP: los correos
externos se encolan y se evalúan a DeadLetter con motivo explícito (no se simula una entrega que no ocurrió).
Sólo se declara PROVEN lo que se probó.