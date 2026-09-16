# AtlasMail — Seguridad

Principios (spec §53): NO OPEN RELAY, NO pérdida silenciosa, NO doble entrega, NO passwords reversibles,
NO cross-tenant access, NO secretos en logs, NO falsa evidencia. Fail-safe.

## Autenticación y sesión
- Login web vía `POST /api/auth/login` → cookie de sesión (`HttpOnly`, `SameSite`, expiración + sliding).
- `[Authorize]` global (deny-by-default) + roles **SuperAdmin / DomainAdmin / SecurityAdmin / HelpDesk / User**.
  - Solo `SuperAdmin` crea dominios. `DomainAdmin` administra buzones/alias dentro de su dominio.
  - `SecurityAdmin` gestiona users/auditoría. Roles vía `IsInRole`/políticas.
- Protección brute-force: contador por (IP, username) con ventana y bloqueo temporal. Se registra
  `LoginAttempt` (éxito/fracaso) y `Auth.LoginFailure` en auditoría.

## Contraseñas
- PBKDF2 (RFC 2898, SHA-256, sal aleatoria de 16B, 100k iteraciones) vía `Pbkdf2PasswordHasher`.
- Formato versionado `1$iterations$salt$hash`; verificación con `FixedTimeEquals`.
- `IPasswordPolicy` por defecto: ≥8, mayúscula, minúscula, dígito, especial. Nunca se almacena el texto plano.

## Autorización / IDOR (spec §33)
- Webmail: cada operación toma el `mailboxId` del usuario autenticado (`ICurrentUser`). Un usuario A
  jamás lee/lista/mueve mensajes del buzón B (los servicios filtran por `mailboxId`).
- Prueba explícita: `tests/.../MailboxServiceIdorTests` (Alice no ve Inbox de Bob) y en integración
  `GET /api/mail/message/{id}` de otro → 404.
- Se responde 404 (no 403) ante recursos ajenos para no revelar existencia.

## Relay (spec §5)
- `RelayPolicy` decide por sobre: anónimo → SOLO dominios/buzones locales válidos; sin destinatario
  local válido o dominio externo → **relay denied**. Autenticado autorizado puede enviar externo.

## Anti-CSRF y headers
- `AddAntiforgery(HeaderName=X-CSRF-TOKEN)`; token por `GET /api/antiforgery/token`, regenerado tras login.
- Headers: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `Content-Security-Policy` restringida; cookies no accesibles por JS.

## Secretos
- No se guarda clave privada, token ni password en logs.
- Auditoría (`AuditEvent`) nunca registra contenido de paswords; sólo acción/actor/IP/resultado/metadata segura.
- La conexión MySQL y el admin inicial vienen de variables de entorno / user-secrets; nunca hardcoded.
- `.gitignore` excluye `mailstore/`, `backups/`, `*.user`, `appsettings.Development.*`.

## Tipos de ataque cubiertos (Ciclo 1)
| Ataque | Mitigación | Dónde se valida |
|---|---|---|
| Open relay | RelayPolicy (anónimo→local only) | Unit `RelayPolicyTests`, SMTP E2E `relay DENEGADO` |
| Cross-tenant IDOR | Filtro por mailboxId del usuario | Unit IDOR + integración 404 |
| Brute-force login | Rate-limit por (IP,user) + LoginAttempt | AuthService |
| Path traversal store | `Path.GetFullPath` + `StartsWith(root)` | FileSystemMessageStore |
| Header injection / CRLF | Parser MIME estricto, builder escapa headers | MimeParser/MimeBuilder |
| XSS email HTML | preview no renderiza; body como texto plano por defecto; CSP | webmail |
| Passwords débiles | IPasswordPolicy | AdminService/unit |
| Pérdida por crash | Persistir antes de 250; lease caduca | cola/worker |

## Pendientes (FASE 2+)
TLS/STARTTLS, AUTH SMTP real, MFA/TOTP, sanitización HTML completa, SPF/DKIM/DMARC efectivos,
antimalware real, cuarentena admin. Ver ROADMAP.