using Kasper.Domain.Entities;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.UnitTests;

public class TimeTests
{
    [Fact]
    public async Task Dates_come_back_from_the_database_as_utc()
    {
        Directory.CreateDirectory(TestEnvironment.Home);
        var path = Path.Combine(TestEnvironment.Home, $"time-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<KasperDbContext>().UseSqlite($"Data Source={path}").Options;
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        Guid id;
        await using (var db = new KasperDbContext(options))
        {
            await db.Database.MigrateAsync();
            var wf = Workflow.Create("me", "x", "x", now);
            id = wf.Id;
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();
        }
        await using (var db = new KasperDbContext(options))
        {
            var loaded = await db.Workflows.SingleAsync(w => w.Id == id);
            Assert.Equal(DateTimeKind.Utc, loaded.CreatedAtUtc.Kind);
            Assert.Equal(now, loaded.CreatedAtUtc);
        }
    }
}
