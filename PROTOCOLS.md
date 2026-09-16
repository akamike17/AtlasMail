# AtlasMail — Protocolos

## SMTP (RFC 5321) — Ciclo 1
Servidor SMTP real (`AtlasMail.Protocols.Smtp.SmtpServer`), state machine multihilo por conexión.

### Comandos soportados
| Comando | Comportamiento |
|---|---|
| EHLO / HELO | Saludo y anuncio de extensiones: SIZE, 8BITMIME, AUTH PLAIN, ENHANCEDSTATUSCODES, HELP |
| MAIL FROM | Abre el sobre; valida sintaxis del path |
| RCPT TO | Valida el sobre con **política de relay** (RCPT local válido → 250; else 550 relay denied) |
| DATA | Recibe el mensaje hasta `.<CR><LF>`; aplica dot-stuffing; invoca el pipeline de ingesta |
| RSET | Reinicia el sobre |
| NOOP | Keep-alive |
| QUIT | Cierra |

### Límites aplicados
- Tamaño máximo de DATA (`Smtp.MaxMessageBytes`, 50 MiB por defecto).
- Timeout de comando y por operación.
- Límite de conexiones por IP (anti-flooding).
- Línea máxima de comando (1000).

### Política de relay (no open relay)
- Conexión anónima (Internet): **sólo** puede entregar a dominios/buzones locales válidos.
  Destinatario inexistente local → 550. Dominio externo anónimo → **550 5.7.1 Relay access denied**.
- Autenticado autorizado (AUTH, FASE 2): puede enviar externamente conforme a política.

### Códigos de respuesta (estado del sobre y mensaje)
- RCPT aceptada → `250 2.1.0 OK`; segura → el handler devuelve `550`/`501`/etc.
- DATA OK → `250 2.0.0 OK queued`; relay → `550 5.7.1`.
- Errores internos → `451 4.3.0 Temporary failure` (retryable).

### STARTTLS / AUTH
Declarado en EHLO (`250-AUTH PLAIN`, `250-SIZE ...`) pero **no implementado operativamente en el Ciclo 1**
(LDAP no llega). Se documenta y marca para FASE 2. No se afirma compatibilidad no probada.

## Cliente SMTP (outbound) — `SmtpClient`
- Esperado para entrega externa MX en FASE 2. Implementa EHLO, MAIL, RCPT, DATA con dot-stuffing, QUIT,
  timeouts estrictos y clasificación temporario/permanente (4xx = retry, 5xx = failed).
- En el Ciclo 1 la entrega externa se encola con estado `DeadLetter` y motivo `external_smtp_delivery_pending_fase2`
  (fail-safe: no se pierde, no se simula entrega). La entrega **local** vía SMTP está PROBADA E2E.

## MIME (RFC 5322 + 2045-2047) — parser y builder
- Parseo: headers, `From/To/Cc/Bcc`, `Subject` (encoded-words UTF-8), fecha, `Message-ID`, `In-Reply-To`.
- Cuerpos: `text/plain`, `text/html`, `multipart/alternative`, `multipart/mixed`.
- Adjuntos: `Content-Disposition: attachment`, `Content-Type`/`name`, `Content-Transfer-Encoding`
  (base64, quoted-printable), nombre saneado, SHA-256 por adjunto.
- Preserva el mensaje original; nunca ejecuta contenido.
- Builder (`MimeBuilder`) genera mensajes válidos (headers escapados, adjuntos en base64).

## Verificación de protocolo
- **Unit**: parser MIME (plano/html/multipart/adjunto/qp/encoded-word), reglas de relay, cuota, retry.
- **Integración SMTP E2E real** (`SmtpE2ETests`): levanta el servidor SMTP real contra MySQL temporal,
  conduce con sockets SMTP reales → RCPT 250/250 OK local; RCPT externo → 550 relay denied.
- **Smoke manual** (cliente SMTP real): `outsider@elsewhere.example → bob@atlas.local` = `250 2.0.0 OK` y
  llega a Inbox; `victim@gmail.com` = relay denied.

## IMAP / Webmail / Búsqueda
- IMAP: **pendiente, FASE 3** (no se declara compatibilidad).
- Webmail: RFC-compatible a nivel de metadatos vía API interna (no protocolo wire).
- Búsqueda `IMessageSearchService`: filtro por sender/recipient/subject/body/date/attachment, aislado por buzón.