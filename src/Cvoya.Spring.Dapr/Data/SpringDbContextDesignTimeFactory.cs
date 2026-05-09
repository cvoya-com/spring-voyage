// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Design-time factory for <see cref="SpringDbContext"/>. Used by the
/// <c>dotnet ef</c> tooling to instantiate the context when generating
/// and scripting migrations. Configures the Npgsql provider so generated
/// migrations target PostgreSQL — the production provider for the
/// Spring Voyage platform.
/// </summary>
/// <remarks>
/// This factory is only invoked by the EF Core tooling. Runtime uses
/// <c>AddCvoyaSpringDapr</c>, which wires the context via DI with a
/// connection string read from configuration. The placeholder connection
/// string used here is never opened; it only has to satisfy Npgsql's
/// parser and pin the provider for migration generation.
/// </remarks>
public class SpringDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SpringDbContext>
{
    /// <inheritdoc />
    public SpringDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SpringDbContext>();
        builder.UseNpgsql(
            "Host=localhost;Database=springvoyage;Username=postgres;Password=postgres",
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "spring"));
        return new SpringDbContext(builder.Options);
    }
}
