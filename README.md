# AtlasMail

Servidor empresarial de correo y colaboración self-hosted y multi-dominio (.NET 8 + MySQL).

**Estado: Ciclo 1 + FASE 2 + FASE 3 + FASE 4 + FASE 5 completadas** (SMTP + IMAP + entrega externa + SPF/DKIM/DMARC + antispam/quarantine/antimalware).

## Documentación
- `ARCHITECTURE.md` — proyectos, pipeline SMTP, cola, seguridad, modelo multi-dominio.
- `INSTALL.md` — instalación reproducible (sin secretos en el repo; admin desde entorno).
- `SECURITY.md` — auth, roles, IDOR, relay, anti-CSRF, matriz de ataques.
- `PROTOCOLS.md` — SMTP server/client, MIME, alcance IMAP.
- `TEST_EVIDENCE.md` — evidencia real de build/tests/smoke E2E (spec §51).
- `ROADMAP.md` — Fases 2–8 posteriores.

## Quickstart
```bash
export ConnectionStrings__DefaultConnection='server=127.0.0.1;port=3306;database=atlasmail;user=Admin;password=TU_PASSWORD;'
export Admin__Username='admin'
export Admin__Password='Atl4smail1!'     # cumple la política (8+ mayús/dígito/especial)
dotnet ef database update --project src/AtlasMail.Infrastructure --startup-project src/AtlasMail.Web
dotnet run --project src/AtlasMail.Web -c Release
```
Web http://localhost:5000 (o el puerto configurado). SMTP local en puerto 2525 (opcional).

## Stack
ASP.NET Core 8 MVC/Razor + `fetch()`, EF Core 8 + Pomelo MySQL, MIME en filesystem (`IMessageStore`),
SMTP server/client propios, worker de cola con lease/claim, PBKDF2, roles. Bootstrap local (sin CDN).