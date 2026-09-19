using System.Collections.Concurrent;
using System.Text;
using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Mime;
using AtlasMail.Domain.Rules;
using AtlasMail.Protocols.Imap;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtlasMail.UnitTests;

/// <summary>
/// Rate-limit SMTP AUTH por IP (§48/§49): per-IP thread-safe sin lock global, expiración+eliminación
/// de entradas, bloqueo tras N fallos, aislamiento entre IPs y no-crecimiento de memoria.
/// </summary>
public class AuthRateLimiterTests
{
    [Fact]
    public void Permite_N_fallos_y_bloquea_el_siguiente()
    {
        var rl = new AuthRateLimiter(maxFailures: 3, window: TimeSpan.FromMinutes(1));
        // N=3 fallos permitidos (cada intento se comprueba y registra al fallar).
        for (int i = 0; i < 3; i++)
        {
            Assert.True(rl.IsAllowed("ip-a"));
            rl.RecordFailure("ip-a");
        }
        // El siguiente (4º) queda bloqueado.
        Assert.False(rl.IsAllowed("ip-a"));
        Assert.Equal(3, rl.Count("ip-a"));
    }

    [Fact]
    public void La_expiracion_vuelve_a_permitir()
    {
        var rl = new AuthRateLimiter(maxFailures: 2, window: TimeSpan.FromMilliseconds(150));
        for (int i = 0; i < 2; i++) { Assert.True(rl.IsAllowed("ip-x")); rl.RecordFailure("ip-x"); }
        Assert.False(rl.IsAllowed("ip-x"));

        Thread.Sleep(200); // la ventana expira
        Assert.True(rl.IsAllowed("ip-x"), "tras expirar la ventana debe volver a permitir");
        // La consulta limpió la entrada inactiva: no debe crecer la memoria.
        Assert.Equal(0, rl.IpCount);
    }

    [Fact]
    public void IP_A_no_bloquea_IP_B()
    {
        var rl = new AuthRateLimiter(maxFailures: 2, window: TimeSpan.FromMinutes(1));
        rl.RecordFailure("ip-a");
        rl.RecordFailure("ip-a");
        Assert.False(rl.IsAllowed("ip-a"), "ip-a agotó su presupuesto");
        Assert.True(rl.IsAllowed("ip-b"), "ip-b no debe verse afectada por ip-a");
    }

    [Fact]
    public void Concurrencia_multi_IP_no_serializa_y_acota_memoria()
    {
        var rl = new AuthRateLimiter(maxFailures: 5, window: TimeSpan.FromMinutes(1));
        var errors = new ConcurrentQueue<Exception>();
        // Muchas IPs en paralelo; cada una falla 3 veces (bajo el límite → permitidas).
        Parallel.For(0, 200, ip =>
        {
            try
            {
                string key = "p" + (ip % 50); // 50 IPs distintas, 4 hilos cada una
                for (int i = 0; i < 3; i++) rl.RecordFailure(key);
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        Assert.Empty(errors);
        // Nunca más entradas que IPs distintas (no crecimiento descontrolado).
        Assert.True(rl.IpCount <= 50, $"entradas={rl.IpCount} debe ser ≤ 50");
        // Cada una de las 50 IPs recibió 200/50 × 3 fallos = 12 (window compartida por IP).
        Assert.Equal(12, rl.Count("p0"));
    }

    [Fact]
    public void TryBegin_es_atomico_por_IP_y_el_exito_libera()
    {
        // §3.md/Fix 2: la reserva de slot es atómica por IP. N conexiones simultáneas compiten por N
        // slots; una vez consumidos, TryBegin devuelve false SIEMPRE hasta CommitSuccess o expiración.
        var rl = new AuthRateLimiter(maxFailures: 3, window: TimeSpan.FromMinutes(1));
        // 3 reservas simultáneas válidas, la 4ª debe estar bloqueada de inmediato (sin esperar a registrar
        // el fallo después del hash, que era la debilidad del patrón IsAllowed-then-RecordFailure).
        Assert.True(rl.TryBegin("ip-z"));
        Assert.True(rl.TryBegin("ip-z"));
        Assert.True(rl.TryBegin("ip-z"));
        Assert.False(rl.TryBegin("ip-z"), "tras 3 reservas, una 4ª conexión concurrente debe bloquearse");
        Assert.Equal(3, rl.Count("ip-z"));

        // Una credencial válida libera el slot (CommitSuccess) → vuelve a permitir.
        rl.CommitSuccess("ip-z");
        Assert.True(rl.TryBegin("ip-z"), "CommitSuccess libera un slot → siguiente intento permitido");
        Assert.Equal(3, rl.Count("ip-z"));
    }

    [Fact]
    public void TryBegin_concurrencia_estricta_respeta_el_limite()
    {
        // §3.md/Fix 2: bajo contención real, a lo sumo MaxFailures reservas concurrentes tienen éxito.
        const int attempts = 64;
        var rl = new AuthRateLimiter(maxFailures: 4, window: TimeSpan.FromMinutes(1));
        var ok = 0; var blocked = 0; var errs = new ConcurrentQueue<Exception>();
        Parallel.For(0, attempts, _ =>
        {
            try
            {
                var allow = rl.TryBegin("ip-race");
                if (allow) System.Threading.Interlocked.Increment(ref ok);
                else System.Threading.Interlocked.Increment(ref blocked);
            }
            catch (Exception ex) { errs.Enqueue(ex); }
        });
        Assert.Empty(errs);
        Assert.Equal(4, ok);        // exactamente 4 slots concedidos (límite)
        Assert.Equal(attempts - 4, blocked); // el resto bloqueado, sin que ninguna "se cuele"
        Assert.Equal(4, rl.Count("ip-race"));
    }

    [Fact]
    public void SweepExpired_elimina_IPs_abandonadas_globalmente()
    {
        // §3.md/Fix 2: la limpieza NO debe depender de que la IP sea re-tocada. El barrido global debe
        // eliminar las entradas de IPs inactivas tras la ventana.
        var rl = new AuthRateLimiter(maxFailures: 5, window: TimeSpan.FromMilliseconds(120), sweepInterval: null);
        rl.RecordFailure("ip-1");
        rl.RecordFailure("ip-2");
        Assert.Equal(2, rl.IpCount);

        Thread.Sleep(200); // la ventana expira y no se vuelve a tocar ninguna IP
        rl.SweepExpired();
        Assert.True(rl.IpCount == 0, "IPs abandonadas tras la ventana deben eliminarse por el barrido global");
    }
}

/// <summary>
/// Sanitización segura del HTML de correos (FASE 9): la salida debe quedar libre de cualquier
/// vector XSS hostil (script, on*, javascript:, iframes/object/forms, style) preservando el
/// texto y el HTML benigno.
/// </summary>
public class HtmlSanitizerTests
{
    private static string S(string html) => HtmlSanitizer.Sanitize(html);

    [Fact]
    public void Script_se_elimina()
    {
        var out1 = S("<script>alert('xss')</script>Hola");
        Assert.DoesNotContain("<script", out1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hola", out1);
    }

    [Fact]
    public void Manejadores_de_evento_se_eliminan()
    {
        var out1 = S("<img src=x onerror=alert(1)>");
        Assert.DoesNotContain("onerror", out1, StringComparison.OrdinalIgnoreCase);

        var out2 = S("<a href='/ok' onclick=\"steal()\">link</a>");
        Assert.DoesNotContain("onclick", out2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/ok\"", out2);
    }

    [Fact]
    public void Urls_javascript_vbscript_y_data_se_bloquean()
    {
        Assert.DoesNotContain("javascript:", S("<a href=\"javascript:alert(1)\">x</a>"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", S("<a href=\"vbscript:msgbox(1)\">x</a>"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", S("<img src=\"data:text/html;base64,PHNjcmlwdD4=\">"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Iframes_object_form_y_style_se_eliminan()
    {
        Assert.DoesNotContain("<iframe", S("<iframe src='https://evil'></iframe>"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", S("<object data='evil'></object>"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", S("<form action='x'><input name='x'></form>texto"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", S("<div style=\"background:url(javascript:1)\">ok</div>"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Codigo_hostil_anidado_no_ejecuta()
    {
        // onclick dentro de un tag "permitido" con atributo estilo bypass.
        var out1 = S("<p onclick=\"evil()\">texto</p>");
        Assert.DoesNotContain("onclick", out1);
        Assert.Contains("<p>", out1); Assert.Contains("texto", out1);
    }

    [Fact]
    public void Html_benigno_se_preserva()
    {
        var out1 = S("<p>Hola <b>mundo</b></p><ul><li>a</li></ul>");
        Assert.Contains("<p>Hola <b>mundo</b></p>", out1);
        Assert.Contains("<ul><li>a</li></ul>", out1);
    }

    [Fact]
    public void Etiqueta_sin_cerrar_se_trata_como_texto()
    {
        // Un "<" roto no debe convertirse en XSS ni romper el resto.
        var out1 = S("<script");
        Assert.DoesNotContain("<script", out1);
    }
}

/// <summary>
/// Puerta TLS del LOGIN IMAP (FASE 9): cuando TLS es obligatorio, LOGIN en claro se rechaza
/// con NO hasta que la sesión negocie STARTTLS (IsTls=true).
/// </summary>
public class ImapLoginTlsGateTests
{
    private static (ImapSession session, MemoryStream ms) NewSession(ImapServerOptions options)
    {
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        var session = new ImapSession(new StubBackend(), writer, options, NullLogger<ImapServer>.Instance);
        return (session, ms);
    }

    private static async Task<string> RunAsync(ImapSession session, MemoryStream ms, string line)
    {
        ms.SetLength(0);
        await session.ExecuteAsync(line, null, CancellationToken.None);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task RequireTlsForLogin_rechaza_LOGIN_en_claro()
    {
        var (session, ms) = NewSession(new ImapServerOptions { RequireTlsForLogin = true });
        string resp = await RunAsync(session, ms, "s1 LOGIN user pass");
        Assert.Contains("NO", resp);
        Assert.Contains("TLS required", resp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LOGIN_tras_STARTTLS_es_permitido()
    {
        var (session, ms) = NewSession(new ImapServerOptions { RequireTlsForLogin = true });
        session.IsTls = true; // simulamos STARTTLS ya negociado
        string resp = await RunAsync(session, ms, "s2 LOGIN user pass");
        Assert.Contains("OK LOGIN completed", resp);
    }

    [Fact]
    public async Task CAPABILITY_anuncia_STARTTLS_solo_con_certificado()
    {
        var (s1, ms1) = NewSession(new ImapServerOptions { TlsCertificate = TestCert() });
        Assert.Contains("STARTTLS", await RunAsync(s1, ms1, "t1 CAPABILITY"));

        var (s2, ms2) = NewSession(new ImapServerOptions { /* sin cert */ });
        Assert.DoesNotContain("STARTTLS", await RunAsync(s2, ms2, "t2 CAPABILITY"));
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 TestCert()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=atlasmail.local", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        byte[] pfx = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw");
        return new System.Security.Cryptography.X509Certificates.X509Certificate2(pfx, "pw",
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.MachineKeySet);
    }

    private sealed class StubBackend : IMailboxBackend
    {
        public Task<MailboxLoginResult?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
            => Task.FromResult<MailboxLoginResult?>(new MailboxLoginResult(1, "user@x", 1));
        public Task<IReadOnlyList<ImapFolder>> ListFoldersAsync(long mailboxId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ImapFolder>>(Array.Empty<ImapFolder>());
        public Task<ImapFolderSnapshot?> SelectFolderAsync(long mailboxId, string folderName, CancellationToken ct = default) => Task.FromResult<ImapFolderSnapshot?>(null);
        public Task<byte[]?> FetchRawAsync(long mailboxId, long folderId, long uid, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task SetFlagsAsync(long mailboxId, long folderId, IReadOnlyList<long> uids, bool? seen, bool? flagged, bool? deleted, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> MoveAsync(long mailboxId, long folderId, long uid, string destinationFolder, CancellationToken ct = default) => Task.FromResult<long?>(null);
        public Task ExpungeAsync(long mailboxId, long folderId, CancellationToken ct = default) => Task.CompletedTask;
    }
}