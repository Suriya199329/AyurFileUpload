using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

internal sealed class DesignTimeLibraryDbFactory : IDesignTimeDbContextFactory<LibraryDb>
{
    public LibraryDb CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<LibraryDb>();
        // Try to read connection string from environment; fall back to a reasonable default used in appsettings.json
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings__FileLibrary")
                   ?? "Server=localhost\\MSSQLSERVER01;Database=AyurHealth;Trusted_Connection=True;TrustServerCertificate=True;";
        builder.UseSqlServer(conn);
        return new LibraryDb(builder.Options);
    }
}
