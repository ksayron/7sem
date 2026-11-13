using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.ServiceDefaults;

public static class PostgresCoordination
{
    // Stable across processes and runtime versions; advisory locks are scoped to a database.
    public static long Key(string resource) => BinaryPrimitives.ReadInt64BigEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes(resource)));

    public static async Task LockTransaction(DbContext db, string resource, CancellationToken ct = default)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A transaction is required for a PostgreSQL transaction lock.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({Key(resource)})", ct);
    }
}
