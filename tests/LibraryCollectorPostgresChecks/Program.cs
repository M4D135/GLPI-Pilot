using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GlpiPilot.Core;
using Npgsql;

// This test may terminate only the uniquely keyed lease it just acquired,
// on the disposable loopback CI database. No production server is accepted.
var connectionString = Environment.GetEnvironmentVariable("GLPI_PILOT_TEST_POSTGRES");
if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("The isolated PostgreSQL test service is required.");
var builder = new NpgsqlConnectionStringBuilder(connectionString);
if (builder.Host is not ("127.0.0.1" or "localhost") || builder.Database != "glpi_pilot_checks" || builder.Username != "glpi_pilot_checks")
    throw new InvalidOperationException("Only the disposable loopback CI database is allowed.");
var root = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-Collector-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_MODE", "1");
Environment.SetEnvironmentVariable("GLPI_PILOT_TEST_ROOT", root);
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var ct = budget.Token;
var preferences = new LibraryPreferences { SyncMode = "postgres", Host = builder.Host, Port = builder.Port,
    Database = builder.Database, Username = builder.Username, TlsMode = "Disable", LocalFolder = Path.Combine(root, "Library") };
var installation = "https://collector.example.invalid/" + Guid.NewGuid().ToString("N") + "/";
var passed = 0;
try
{
    await using var observer = new NpgsqlConnection(connectionString);
    await observer.OpenAsync(ct);
    await using (var lease = await LibraryCollectorLease.AcquireAsync(preferences, installation, builder.Password ?? "", ct))
    {
        await lease.VerifyAsync(ct);
        var refused = false;
        try { await using var duplicate = await LibraryCollectorLease.AcquireAsync(preferences, installation, builder.Password ?? "", ct); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "Two independent PostgreSQL sessions cannot collect the same installation concurrently.");
        await using var other = await LibraryCollectorLease.AcquireAsync(preferences, installation + "other/", builder.Password ?? "", ct);
        await other.VerifyAsync(ct);
        Check(true, "Different installations retain separate collector leases.");
    }
    await using (var reacquired = await LibraryCollectorLease.AcquireAsync(preferences, installation, builder.Password ?? "", ct))
    {
        await reacquired.VerifyAsync(ct);
        Check(true, "Disposal releases the session lock so another collector can resume.");
    }
    await using (var lost = await LibraryCollectorLease.AcquireAsync(preferences, installation, builder.Password ?? "", ct))
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("glpi-pilot-collector-v1\n" + installation));
        var high = (long)BinaryPrimitives.ReadUInt32BigEndian(hash);
        var low = (long)BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(4));
        await using var locate = new NpgsqlCommand("SELECT pid FROM pg_locks WHERE locktype='advisory' AND granted AND classid::bigint=@high AND objid::bigint=@low AND objsubid=1 AND database=(SELECT oid FROM pg_database WHERE datname=current_database())", observer);
        locate.Parameters.AddWithValue("high", high); locate.Parameters.AddWithValue("low", low);
        var value = await locate.ExecuteScalarAsync(ct);
        if (value is not int pid || pid <= 0 || pid == observer.ProcessID) throw new InvalidOperationException("The uniquely keyed test lease could not be identified.");
        await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid,5000)", observer);
        terminate.Parameters.AddWithValue("pid", pid);
        if (!Equals(await terminate.ExecuteScalarAsync(ct), true)) throw new InvalidOperationException("The isolated test session did not terminate.");
        var refused = false;
        using (LibraryCollectionPacer.Enter(new(), verify: token => lost.VerifyAsync(token)))
        {
            try { await LibraryCollectionPacer.BeforeNavigationAsync(ct); }
            catch (InvalidOperationException) { refused = true; }
        }
        Check(refused, "Losing the actual PostgreSQL session blocks the next GLPI navigation instead of falling back locally.");
    }
    await using (var recovered = await LibraryCollectorLease.AcquireAsync(preferences, installation, builder.Password ?? "", ct))
    {
        await recovered.VerifyAsync(ct);
        Check(true, "A new explicitly acquired session can resume after the previous owner disconnected.");
    }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel(); var refused = false;
        try { await using var never = await LibraryCollectorLease.AcquireAsync(preferences, installation, builder.Password ?? "", cancelled.Token); }
        catch (OperationCanceledException) { refused = true; }
        Check(refused, "Pre-cancellation cannot acquire a collector lease.");
    }
    Console.WriteLine($"{passed}/6 PostgreSQL collector lease checks passed.");
    return passed == 6 ? 0 : 1;
}
finally
{
    NpgsqlConnection.ClearAllPools();
    try { Directory.Delete(root, true); } catch (IOException) { }
}
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    passed++; Console.WriteLine("PASS PostgreSQL collector: " + label);
}
