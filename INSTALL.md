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

## 5. Ejecutar
```bash
# Web (admin + webmail) — arranca también SMTP (si enabled) y el worker de cola
dotnet run --project src/AtlasMail.Web -c Release

# Worker independiente (opcional si se quiere proceso separado)
dotnet run --project src/AtlasMail.Worker -c Release
```

La Web levanta internamente el DeliveryWorker (cola). Para producción puede ejecutarse el Worker
como proceso aparte apuntando a la misma DB/message-store.

## 6. Verificación de humo
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