# AtlasMail — ROADMAP

Estado: **Ciclo 1 completado** (vertical slice funcional). Sección §52 del spec maestro (1.md).

## CICLO 1 — COMPLETADO ✅
- Arquitectura modular (`src/*` + `tests/*`, .NET 8, MySQL/Pomelo).
- Dominio/buzones/alias/usuarios, roles, multi-dominio, plus addressing, catch-all (disabled default).
- SMTP local real + webmail + cola (lease/claim + retry/backoff) + auditoría + message trace + backup/restore.
- Deny-by-default, antiforgery, IDOR controlado, PBKDF2, relay policy.
- Build 0/0; unit 56 + integration 13; SMTP E2E real y relay denied probados.

## FASE 2 — SMTP robusto + entrega externa
- Entrega externa real: resolver MX (`IMxResolver`), entregar con `SmtpClient`, manejar 4xx/5xx.
- STARTTLS y AUTH (PLAIN/LOGIN) operativos; política de envío autenticado.
- Bounces/DSN seguros (sin backscatter); grandes archivos (límites); rate limits por IP/usuario/dominio.
- Observabilidad: métricas de cola, health checks de DNS.

## FASE 3 — IMAP y clientes externos
- IMAP incremental: login, listar/select carpetas, listar/obtener mensajes, flags, mover, eliminar.
- Probar con Thunderbird/Outlook/Apple Mail (compatibilidad PROVEN solo tras prueba real).

## FASE 4 — Autenticación de correo (SPF/DKIM/DMARC)
- Verificación SPF en recepción; firma/verificación DKIM; evaluación DMARC con alignment.
- Administración: mostrar registros DNS a publicar por dominio.

## FASE 5 — Antispam / quarantine / antimalware
- Scoring ampliado (reputación, historial, MDMF); cuarentena administrable y liberación auditada.
- Antimalware real (ClamAV u otro) sustituyendo al `NoOpAttachmentScanner` (NoOp etiquetado,
  nunca marca Unknown como Clean).

## FASE 6 — Calendar / Contacts / Groups
- Calendario (día/semana/mes, `.ics`), contactos (VCARD/CSV), listas de distribución y grupos con políticas.

## FASE 7 — Alta disponibilidad / replicación / observabilidad avanzada
- Múltiples workers, reaprovechamiento de lease, replicación de message store, métricas/paneles.

## FASE 8 — IA opcional y desacoplada
- `IMailIntelligenceService` (Disabled por defecto): resumen, clasificación, prioridad, phishing asistido,
  búsqueda semántica. Sin IA el servidor funciona completo.

## Principios permanentes (§53)
NO open relay · NO pérdida silenciosa · NO doble entrega · NO passwords reversibles ·
NO cross-tenant access · NO secretos en logs · NO falsa evidencia. Fail-safe.