using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kasper.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef migrations add`.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KasperDbContext>
{
    public KasperDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KasperDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
