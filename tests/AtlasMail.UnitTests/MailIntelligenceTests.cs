using AtlasMail.Security.MailIntelligence;
using Xunit;

namespace AtlasMail.UnitTests;

public class MailIntelligenceTests
{
    private readonly LocalMailIntelligenceService _ai = new();
    private static AtlasMail.Application.Abstractions.SubjectBody B(string subj, string body)
        => new(subj, body);

    [Fact]
    public void Clasifica_finanzas_y_urgencia()
    {
        var fin = _ai.Classify(B("Factura vencida", "Su factura tiene un pago pendiente de 5000 MXN."));
        Assert.Equal(AtlasMail.Application.Abstractions.MailIntelligenceCategory.Finance, fin);

        var urg = _ai.Classify(B("urgente acción requerida crítica", "deadline hoy vencimiento."));
        Assert.Equal(AtlasMail.Application.Abstractions.MailIntelligenceCategory.Urgent, urg);
    }

    [Fact]
    public void Prioridad_refleja_urgencia_y_seguridad()
    {
        int low = _ai.Priority(B("Hola", "notas de reunión"));
        int high = _ai.Priority(B("URGENTE acción requerida antes de hoy", "cuenta bloqueada, verificación crítica alert"));
        Assert.True(high > low, $"high={high} low={low}");
        Assert.InRange(low, 0, 100);
        Assert.InRange(high, 0, 100);
    }

    [Fact]
    public void Phishing_asistido_detecta_señales()
    {
        var ph = _ai.AssessPhishing(B("Verify your account", "Your account will be suspended. Confirm your password immediately."));
        Assert.True(ph.Score > 0, "debe marcar señales");
        Assert.NotEmpty(ph.Signals);

        var clean = _ai.AssessPhishing(B("Reunion", "Gracias por tu nota."));
        Assert.Equal(0, clean.Score);
        Assert.Empty(clean.Signals);
    }

    [Fact]
    public void Resumen_extractivo_acorta()
    {
        var longBody = "Este correo contiene instrucciones importantes. Por favor revisa el anexo. " +
                       "Necesitamos tu confirmacion para continuar con el proyecto. Gracias de antemano.";
        var sum = _ai.Summarize(B("Asunto", longBody), 40);
        Assert.NotEmpty(sum);
        Assert.True(sum.Length <= 42, $"len={sum.Length}");
    }

    [Fact]
    public void Disabled_no_analiza()
    {
        var d = new DisabledMailIntelligence();
        Assert.False(d.Enabled);
        Assert.Equal(0, d.Priority(B("x", "y")));
        Assert.Equal(0, d.AssessPhishing(B("x", "verify your account now")).Score);
    }
}