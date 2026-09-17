using System.Text;
using AtlasMail.Application;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.UnitTests;

public class CalendarServiceTests
{
    private static (FakeAppDbContext ctx, CalendarService svc, MailDomain domain, Mailbox mb) Setup(string tag)
    {
        var ctx = new FakeAppDbContext("cal_" + tag + Guid.NewGuid().ToString("N"));
        var domain = new MailDomain { Name = tag + ".local", Enabled = true, MaxMailboxQuotaBytes = 1024, MaxMessageSizeBytes = 1024, MaxRecipientsPerMessage = 10 };
        var mb = new Mailbox { Domain = domain, LocalPart = "user", PasswordHash = "h", QuotaBytes = 1024 };
        ctx.Domains.Add(domain); ctx.Mailboxes.Add(mb);
        ctx.SaveChangesAsync().Wait();
        return (ctx, new CalendarService(ctx), domain, mb);
    }

    [Fact]
    public async Task Crear_listar_y_eliminar_evento()
    {
        var (_, svc, _, mb) = Setup("ce");
        var e = await svc.CreateAsync(mb.Id, new NewEventInput("Reunión", new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 20, 11, 0, 0, DateTimeKind.Utc),
            "discutir", "Sala 1", "America/Mexico_City", "org@x.local", null, "confirmed", false, new[] { "ana@x.local", "juan@x.local" }));
        Assert.Equal("Reunión", e.Title);
        Assert.Equal(2, e.Attendees.Count);

        var list = await svc.ListAllAsync(mb.Id);
        Assert.Single(list);

        Assert.True(await svc.DeleteAsync(mb.Id, e.Id));
        Assert.Empty(await svc.ListAllAsync(mb.Id));
    }

    [Fact]
    public async Task Export_ics_y_import_es_idempotente()
    {
        var (ctx, svc, _, mb) = Setup("icx");
        await svc.CreateAsync(mb.Id, new NewEventInput("Evento ICS", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            "desc", "Lugar", null, null, null, "confirmed", false, null));

        var ics = await svc.ExportIcsAsync(mb.Id);
        Assert.Contains("BEGIN:VCALENDAR", ics);
        Assert.Contains("SUMMARY:Evento ICS", ics);
        Assert.Contains("DTSTART:20261001T090000Z", ics);

        // nuevo buzón y reimportar el .ics
        var (ctx2, svc2, _, mb2) = Setup("icx2");
        var res = await svc2.ImportIcsAsync(mb2.Id, ics);
        Assert.Equal(1, res.Imported);
        var again = await svc2.ImportIcsAsync(mb2.Id, ics);
        Assert.Equal(0, again.Imported); // idempotente por UID
        var evs = await svc2.ListAllAsync(mb2.Id);
        Assert.Single(evs);
        Assert.Equal("Evento ICS", evs[0].Title);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), evs[0].StartUtc);
    }

    [Fact]
    public async Task Rango_filtra_por_fechas()
    {
        var (_, svc, _, mb) = Setup("cr");
        await svc.CreateAsync(mb.Id, new NewEventInput("Junta", new DateTime(2026, 9, 5), new DateTime(2026, 9, 5, 1, 0, 0), null, null, null, null, null, "confirmed", false, null));
        var inRange = await svc.ListByRangeAsync(mb.Id, new DateTime(2026, 9, 1), new DateTime(2026, 9, 10));
        var outRange = await svc.ListByRangeAsync(mb.Id, new DateTime(2026, 10, 1), new DateTime(2026, 10, 10));
        Assert.Single(inRange);
        Assert.Empty(outRange);
    }
}

public class ContactServiceTests
{
    private static (FakeAppDbContext ctx, ContactService svc, Mailbox mb) Setup(string tag)
    {
        var ctx = new FakeAppDbContext("con_" + tag + Guid.NewGuid().ToString("N"));
        var domain = new MailDomain { Name = tag + ".local", Enabled = true, MaxMailboxQuotaBytes = 1024, MaxMessageSizeBytes = 1024, MaxRecipientsPerMessage = 10 };
        var mb = new Mailbox { Domain = domain, LocalPart = "user", PasswordHash = "h", QuotaBytes = 1024 };
        ctx.Domains.Add(domain); ctx.Mailboxes.Add(mb);
        ctx.SaveChangesAsync().Wait();
        return (ctx, new ContactService(ctx), mb);
    }

    [Fact]
    public async Task Crud_contacto_personal()
    {
        var (_, svc, mb) = Setup("crud");
        var c = await svc.CreatePersonalAsync(mb.Id, new ContactInput("Ana Paz", "ana@corp.local", "555", "Corp", "nota"));
        Assert.Equal("Ana Paz", c.Name);

        var list = await svc.ListPersonalAsync(mb.Id);
        Assert.Single(list);

        var upd = await svc.UpdateAsync(mb.Id, c.Id, new ContactInput("Ana Paz G", "ana@corp.local", "555", "Corp", "nota2"));
        Assert.Equal("Ana Paz G", upd.Name);

        Assert.True(await svc.DeleteAsync(mb.Id, c.Id));
        Assert.Empty(await svc.ListPersonalAsync(mb.Id));
    }

    [Fact]
    public async Task Export_import_vcard()
    {
        var (_, svc, mb) = Setup("vcf");
        await svc.CreatePersonalAsync(mb.Id, new ContactInput("Juan Pérez", "juan@x.local", "555-0100", "ACME", "cliente"));
        var vcf = await svc.ExportVcfAsync(mb.Id);
        Assert.Contains("BEGIN:VCARD", vcf);
        Assert.Contains("FN:Juan Pérez", vcf);

        var (ctx2, svc2, mb2) = Setup("vcf2");
        var res = await svc2.ImportVcfAsync(mb2.Id, vcf);
        Assert.Equal(1, res.Imported);
        var list = await svc2.ListPersonalAsync(mb2.Id);
        Assert.Single(list);
        Assert.Equal("juan@x.local", list[0].Email);
    }

    [Fact]
    public async Task Import_csv_con_campos_comas()
    {
        var (ctx, svc, mb) = Setup("csv");
        var csv = "Name,Email,Phone,Company,Notes\nLuis,luis@x.local,\"555,125\",ACME,ok\n";
        var res = await svc.ImportCsvAsync(mb.Id, csv);
        Assert.Equal(1, res.Imported);
        var list = await svc.ListPersonalAsync(mb.Id);
        Assert.Single(list);
        Assert.Equal("555,125", list[0].Phone);
    }
}

public class GroupServiceTests
{
    private static async Task<(FakeAppDbContext ctx, GroupService svc, MailDomain domain)> Setup(string tag)
    {
        var ctx = new FakeAppDbContext("grp_" + tag + Guid.NewGuid().ToString("N"));
        var domain = new MailDomain { Name = tag + ".local", Enabled = true, MaxMailboxQuotaBytes = 1024, MaxMessageSizeBytes = 1024, MaxRecipientsPerMessage = 10 };
        ctx.Domains.Add(domain);
        await ctx.SaveChangesAsync();
        return (ctx, new GroupService(ctx), domain);
    }

    [Fact]
    public async Task Crear_lista_y_expandir_miembros()
    {
        var (_, svc, domain) = await Setup("ventas");
        var g = await svc.CreateAsync(domain.Id, new GroupInput("Ventas", "ventas", "lista ventas", true, "internal", false, 0));
        await svc.AddMemberAsync(domain.Id, g.Id, "ana@ventas.local", "Ana");
        await svc.AddMemberAsync(domain.Id, g.Id, "juan@ventas.local", "Juan");

        var expanded = await svc.ExpandAsync("ventas@ventas.local");
        Assert.Equal(2, expanded.Count);
        Assert.Contains("ana@ventas.local", expanded);
        Assert.Contains("juan@ventas.local", expanded);
    }

    [Fact]
    public async Task Loops_anidados_se_previenen()
    {
        var (_, svc, domain) = await Setup("loop");
        // ventas -> (ana, devs); devs -> (uly, ventas) => ciclo ventas<->devs
        var ventas = await svc.CreateAsync(domain.Id, new GroupInput("Ventas", "ventas", null, true, "internal", false, 0));
        var devs = await svc.CreateAsync(domain.Id, new GroupInput("Devs", "devs", null, true, "internal", false, 0));
        await svc.AddMemberAsync(domain.Id, ventas.Id, "ana@loop.local", null);
        await svc.AddMemberAsync(domain.Id, ventas.Id, "devs@loop.local", null);
        await svc.AddMemberAsync(domain.Id, devs.Id, "uly@loop.local", null);
        await svc.AddMemberAsync(domain.Id, devs.Id, "ventas@loop.local", null); // ciclo

        var expanded = await svc.ExpandAsync("ventas@loop.local");
        // ana + uly + ventas(loop) termina; no infinito, dedupe
        Assert.Contains("ana@loop.local", expanded);
        Assert.Contains("uly@loop.local", expanded);
        Assert.True(expanded.Count <= 3, "no duplica ni entra en loop");
    }

    [Fact]
    public async Task Direccion_en_uso_por_buzon_se_rechaza()
    {
        var (ctx, svc, domain) = await Setup("col");
        ctx.Mailboxes.Add(new Mailbox { Domain = domain, LocalPart = "ventas", PasswordHash = "h", QuotaBytes = 1024 });
        await ctx.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateAsync(domain.Id, new GroupInput("V", "ventas", null, true, "internal", false, 0)));
    }

    [Fact]
    public async Task Politica_internal_by_default()
    {
        var (_, svc, domain) = await Setup("pol");
        var g = await svc.CreateAsync(domain.Id, new GroupInput("G", "g", null, true, "external", true, 10));
        Assert.Equal("ExternalAllowed", g.SendPolicy);
        Assert.True(g.ModerationEnabled);
        Assert.Equal(10, g.MaxMembers);
    }
}