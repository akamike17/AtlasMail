using AtlasMail.Application.Abstractions;
using Xunit;

namespace AtlasMail.UnitTests;

public class MetricsRegistryTests
{
    [Fact]
    public void Increment_y_snapshot_acumulan()
    {
        var m = new MetricsRegistry();
        m.Increment("mail.received");
        m.Increment("mail.received");
        m.Increment("auth.failures", 5);
        var snap = m.Snapshot();
        Assert.Equal(2, snap["mail.received"]);
        Assert.Equal(5, snap["auth.failures"]);
    }

    [Fact]
    public void Gauge_y_timer_se_registran()
    {
        var m = new MetricsRegistry();
        m.SetGauge("queue.pending", 7);
        m.Observe("delivery", System.TimeSpan.FromMilliseconds(120));
        m.Observe("delivery", System.TimeSpan.FromMilliseconds(80));
        var snap = m.Snapshot();
        Assert.Equal(7, snap["queue.pending"]);
        Assert.Equal(2, snap["delivery_count"]);
        Assert.Equal(200, snap["delivery_sum_ms"]);
    }

    [Fact]
    public void Formato_texto_sanitiza_nombres()
    {
        var m = new MetricsRegistry();
        m.Increment("mail.messages_received");
        m.SetGauge("storage.bytes", 1024);
        var text = MetricsFormat.ToText(m.Snapshot());
        Assert.True(text.Contains("mail_messages_received 1"), $"TEXT=[{text}]");
        Assert.True(text.Contains("storage_bytes 1024"), $"TEXT=[{text}]");
        Assert.DoesNotContain("password", text);
    }

    [Fact]
    public void Thread_safe_con_muchos_incrementos()
    {
        var m = new MetricsRegistry();
        Parallel.For(0, 1000, _ => m.Increment("mail.received"));
        Assert.Equal(1000, m.Snapshot()["mail.received"]);
    }
}