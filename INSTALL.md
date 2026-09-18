# AtlasMail — Instalación

Procedimiento reproducible (spec §45, §46). No hay usuarios/passwords hardcoded; el admin se crea en
el primer arranque a partir de configuración o variables de entorno.

## Requisitos
- .NET 8 SDK (probado con 8.0.425).
- MySQL o MariaDB 8.x local (probado con MySQL 8.0.46). Puerto 3306.
- El host puede ser Windows o Linux; los comandos usan sintaxis de shell.

## 1. Configurar la conexión (sin secretos en el repo)
`appsettings.json` usa el placeholder `__OVERRIDE_IN_ENV_OR_SECRETS__`. Provee la conexión por
**variable de entorno** (tiene precedencia) o por user-secrets:

```bash
export ConnectionStrings__DefaultConnection='server=127.0.0.1;port=3306;database=atlasmail;user=Admin;password=TU_PASSWORD;'
```

Para migraciones EF design-time usa:
```bash
export ATLASMAIL_CONNECTION_STRING='server=127.0.0.1;port=3306;database=atlasmail;user=Admin;password=TU_PASSWORD;'
```

## 2. Crear y migrar la base de datos
```bash
# La migración ya está versionada (Persistence/Migrations/InitialCreate).
dotnet ef database update --project src/AtlasMail.Infrastructure --startup-project src/AtlasMail.Web
```
La base `atlasmail` se crea automáticamente. El arranque también aplica migraciones pendientes
(`Database.Migrate()` en Program), por lo que `fresh install` y `second startup` PASAN sin pasos extra.

## 3. Bootstrap administrativo (primer arranque — controlado)
El SuperAdmin se crea solo si `Admin:Username`/`Admin:Password` están definidos. Nunca hay password
por defecto.

```bash
export Admin__Username='admin'
export Admin__Password='UnaContras3na!Segura'
```

> La contraseña debe cumplir la política por defecto: mínimo 8, mayúscula, minúscula, dígito y carácter
> especial (ej. `Atl4smail1!`).

## 4. Puerto y SMTP
```bash
export ASPNETCORE_URLS='http://0.0.0.0:5000'
export Smtp__Enabled=1         # 0 lo apaga
export Smtp__Port=2525
export Smtp__Hostname='mail.midominio.com'
```
Opciones de endurecimiento del servidor SMTP (FASE 9):
```bash
export Smtp__MaxMessageBytes=52428800          # límite de mensaje (MIME crudo)
export Smtp__MaxCommandsPerConnection=1000     # flood de comandos → 421
export Smtp__AuthFailuresPerIpMax=10           # brute-force AUTH por IP
export Smtp__AuthFailureWindowMinutes=15       # ventana del contador anti-brute-force
export Smtp__DataTimeout=600                   # segundos; límite absoluto de la fase DATA (stream infinito)
export Smtp__TlsCertificatePath='/etc/ssl/mail.pfx'   # opcional; habilita STARTTLS
export Smtp__TlsCertificatePassword=''         # password del PFX (vacío si no tiene)
export Smtp__RequireTls=1                      # 1 = exige TLS antes de AUTH (rechaza 530 en claro)
```
Con `Smtp__RequireTls=1` y un certificado, el AUTH en claro se rechaza y no se anuncia hasta negociar STARTTLS.
Sin certificado, STARTTLS no se anuncia y `RequireTls` no puede exigir AUTH cifrado (config no aceptable).

## 5. Entrega externa (FASE 2)
La entrega a dominios externos resuelve MX real (DNS del sistema; DnsClient). Configuración opcional:

```bash
export Delivery__ConnectTimeoutSeconds=60        # timeout por conexión SMTP
export Delivery__MaxRetries=6                    # reintentos máximos por mensaje
export Delivery__StartTlsRequired=false          # true exige STARTTLS (DANE/estricto)
export Delivery__RateLimitPerMinute=0            # 0 = sin límite (mensajes/min por remitente)
export Delivery__RateLimitPerDomainPerMinute=0   # 0 = sin límite (mensajes/min por dominio destino)
export Delivery__HeloName='mail.midominio.com'   # EHLO del MTA saliente
export Delivery__DnsProbeEnabled=true            # health: resuelve MX periódicamente
export Delivery__DnsProbeDomain='gmail.com'      # dominio que usa el probe de DNS
export Delivery__WorkerEnabled=1                 # worker de cola de salida (0 desactiva; tests/CI sin cola)
```
El worker de fondo (`DeliveryWorker`) es el que procesa la cola de salida con `Delivery:Concurrency` (default 4).
En producción va activo (default 1); en pruebas/CI sin cola de entrega se apaga con `0` para no dejar un worker
reintentando contra una BD ya descartada.

Los fallos permanentes (5xx) generan un DSN/bounce al remitente **solo** si es un buzón local válido
(anti-backscatter). La resolución DNS real no requiere configuración (usa los servidores del sistema).

## 6. Autenticación de correo (FASE 4)
SPF/DKIM/DMARC en recepción y firma DKIM en salida:

```bash
export Delivery__DmarcEnforce=true   # false audit-only: DMARC fail se suma al score pero no rechaza/cuarentena
```

Administración por dominio (SuperAdmin): el Admin Center ya expone
`GET /api/admin/domain/{id}/auth` (registros DNS a publicar), `POST .../auth/dkim/enable` (genera clave
DKIM y el registro `atlasmail._domainkey.<dominio>` TXT) y `POST .../auth/dmarc` (`"none"|"quarantine"|"reject"`).

Registros a publicar en tu DNS:
- **SPF**: `v=spf1 mx <tus-IPs> ~all` en el TXT del dominio.
- **DKIM**: `v=DKIM1; k=rsa; p=<clave-pública>` en `atlasmail._domainkey.<dominio>` TXT.
- **DMARC**: `v=DMARC1; p=<policy>; adkim=r; aspf=r; fo=1` en `_dmarc.<dominio>` TXT.

## 7. Antispam / antimalware / cuarentena (FASE 5)
- **Antimalware**: por defecto se usa un scanner heurístico local (`HeuristicAttachmentScanner`, sin API
  comercial). Detecta ejecutables/scripts, double-extension, magic bytes y macros de Office. Lo que no
  reconoce queda en **Unknown** (nunca Clean). Para firmas reales se puede pluguear ClamAV detrás de
  `IAttachmentScanner`.
- **Cuarentena (§22)**: los mensajes marcados Spam/Quarantine/Malicious se aíslan en cuarentena
  (no visibles en el buzón). SecurityAdmin/SuperAdmin puede listar, inspeccionar, **liberar**, **eliminar**
  y **bloquear remitente** desde el Admin Center:
  - `GET  /api/admin/quarantine` — lista
  - `GET  /api/admin/quarantine/{id}` — inspección (sin MIME completo)
  - `POST /api/admin/quarantine/{id}/release` — liberar al buzón
  - `DELETE /api/admin/quarantine/{id}` — eliminar (borra metadata + blob)
  - `POST /api/admin/quarantine/block` `{value, kind:"exact"|"domain", reason}` — bloquear remitente
  - `GET /api/admin/quarantine/blocked` / `DELETE /api/admin/quarantine/blocked/{id}` — gestionar blocklist
- El remitente bloqueado se rechaza ya en la recepción (antes de persistir).

## 8. Calendario / contactos / grupos (FASE 6)
Colaboración por webmail, con interoperabilidad estándar:
- **Calendario**: `GET/POST/PUT/DELETE /api/personal/calendar[...]`,
  `GET /api/personal/calendar/export.ics`, `POST /api/personal/calendar/import.ics`.
  Import/export `.ics` (RFC 5545) idempotente.
- **Contactos**: `GET/POST/PUT/DELETE /api/personal/contacts[...]`,
  `GET /api/personal/contacts/export.vcf|.csv`, `POST /api/personal/contacts/import.vcf|.csv`.
- **Listas de distribución** (admin): `ventas@empresa.mx → ana@, juan@, maria@`.
  - `GET/POST/PUT/DELETE /api/admin/domain/{id}/groups[...]`
  - `GET /api/admin/groups/expand/{localPart}/{domain}`
  - Desde el SMTP, una lista local se acepta como destinatario y se expande en la ingesta
    entregando una copia a cada miembro con buzón local.
  - Políticas: envío interno/externo, moderación opcional, límite de miembros; anti-loop en la expansión.

## 9. Alta disponibilidad y observabilidad (FASE 7)
- **Worker concurrente**: el `DeliveryWorker` procesa hasta `Delivery:Concurrency` items en paralelo
  (default 4). Cada item usa un lease/claim atómico MySQL; si el worker muere, el lease caduca y el
  item vuelve a procesable (sin pérdida). Puedes lanzar varios workers sobre la misma BD/store.
- **Métricas (sin datos sensibles)**:
  - `GET /api/metrics` → JSON con contadores/gauges/timers.
  - `GET /api/metrics/text` → texto plano estilo Prometheus (nombres sanitizados).
- Config: `Delivery__Concurrency=4`, `Delivery__Hostname=atlasmail.local`, `Delivery__LoopIntervalMs=2000`.

## 10. IA opcional (FASE 8)
IA **local y desacoplada** (spec §31): el servidor funciona perfectamente sin ella. Por defecto deshabilitada.
- IA local (heurística, sin red): `Ai__Enabled=true`. Prioridad, clasificación, phishing asistido, resumen.
- IA con backend LLM (**OpenAI-compatible**) para funciones avanzadas:
  ```
  export Ai__Enabled=true
  export Ai__Backend__Enabled=true
  export Ai__Backend__Endpoint='https://<proveedor>'
  export Ai__Backend__ApiKey='<key>'
  export Ai__Backend__Model='gpt-4o-mini'
  ```
- **Doble consentimiento** (§31): el backend sólo se usa si además el buzón dio consentimiento vía
  `POST /api/personal/ai/consent` `{grant:true}` (por defecto falso). Ningún contenido se envía a un
  proveedor sin ambas condiciones.
- Endpoints webmail (requieren sesión): `POST /api/personal/ai/analyze` (local), `ai/translate`,
  `ai/suggest-reply`, `ai/draft`, `ai/classify`, `ai/semantic-search`, `GET/POST ai/consent`.

## 11. IMAP (FASE 3)
Servidor IMAP4rev1 (login, listar/select carpetas, listar y obtener mensajes, flags, mover, eliminar):

```bash
export Imap__Enabled=1          # 0 lo apaga (puerto 143 por defecto)
export Imap__Port=143
export Imap__Hostname='mail.midominio.com'
export Imap__MaxMessageBytes=52428800
```

Clientes: host `mail.midominio.com`, puerto 143, IMAP normal (sin autenticación SSL aún; se puede añadir
STARTTLS/IMAPS en una fase posterior). Autenticación con el **password del buzón** (mismo que SMTP AUTH).

## 12. Ejecutar
```bash
# Web (admin + webmail) — arranca también SMTP / IMAP (si enabled) y el worker de cola
dotnet run --project src/AtlasMail.Web -c Release

# Worker independiente (opcional si se quiere proceso separado)
dotnet run --project src/AtlasMail.Worker -c Release
```

La Web levanta internamente el DeliveryWorker (cola). Para producción puede ejecutarse el Worker
como proceso aparte apuntando a la misma DB/message-store.

## 13. Verificación de humo
- `GET /health` → 200.
- `GET /Account/Login` → 200 HTML.
- Login del admin → redirect a `/Admin` (dashboard).
- (Opcional) cliente SMTP contra `localhost:2525`: entrega local → 250; relay externo anónimo → 550.

## Almacenamiento
- Metadatos → MySQL.
- MIME crudo → `Storage:Path` (por defecto `mailstore/` junto al binario).
- Backups → `Storage:BackupPath` (por defecto `backups/`).

Ambos directorios están en `.gitignore`. Ajusta con
`export Storage__Path='/var/lib/atlasmail/store'` y `export Storage__BackupPath='/var/lib/atlasmail/backups'`.

## Notas de producción (spec §46)
- No demo data, no password por defecto, sin migraciones destructivas automáticas.
- Fuerza HTTPS/TLS según el despliegue.
- No hay secretos en el repo (conexión y admin vienen de entorno/secrets).