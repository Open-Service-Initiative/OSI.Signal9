using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OSI.Signal9.Server.Data;

/// <summary>
/// Lets the EF Core tools build the context without starting the API. Commands that reach a database take
/// <c>--connection</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<Signal9DbContext>
{
    public Signal9DbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<Signal9DbContext>().UseNpgsql().Options);
}
