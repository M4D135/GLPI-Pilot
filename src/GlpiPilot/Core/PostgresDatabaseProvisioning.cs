using System.Text;
using Npgsql;

namespace GlpiPilot.Core;

public sealed record PostgresProvisionResult(bool DatabaseCreated, bool AccountCreated, string Database, string Account, string Message);

public static class PostgresDatabaseProvisioning
{
    private static readonly string[] Tables = ["glpi_pilot_library_schema", "glpi_pilot_library_tickets", "glpi_pilot_library_changes", "glpi_pilot_library_receipts", "glpi_pilot_library_conflicts", "glpi_pilot_library_history", "gp_shared_metadata", "gp_shared_items", "gp_shared_changes", "gp_shared_conflicts", "gp_shared_receipts", "gp_shared_batch_receipts"];
    private static string Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 63)
            throw new InvalidDataException("Le nom de base et le compte PostgreSQL doivent contenir entre 1 et 63 octets UTF-8, sans caractère de contrôle.");
        return value;
    }
    private static string Quote(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(Name(value));
    private static async Task<object?> Scalar(NpgsqlConnection db, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, db); foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        return await command.ExecuteScalarAsync(ct);
    }
    private static async Task Execute(NpgsqlConnection db, string sql, CancellationToken ct)
    { await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(ct); }

    private static async Task EnsureAgentRightsAsync(NpgsqlConnection db, string account, CancellationToken ct)
    {
        // Inspect before initializing either schema: a rejected account must never
        // cause schema writes merely because it was selected in the Admin form.
        var elevated = (bool)(await Scalar(db, """
            SELECT has_database_privilege(@account,current_database(),'CREATE')
                OR has_schema_privilege(@account,'public','CREATE')
                OR EXISTS(SELECT 1 FROM pg_namespace n JOIN pg_roles r ON r.oid=n.nspowner WHERE r.rolname=@account)
                OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_roles r ON r.oid=c.relowner
                          JOIN pg_namespace n ON n.oid=c.relnamespace WHERE r.rolname=@account AND n.nspname='public')
            """, ct, ("account", account)))!;
        if (elevated) throw new InvalidDataException("Le compte technique possède des objets ou des droits CREATE dans cette base. Utilisez un compte dédié sans propriété ni création de schéma. Si CREATE est accordé à PUBLIC, faites corriger ce droit par l’administrateur ; aucun droit existant n’a été révoqué.");
        var dangerous = (bool)(await Scalar(db, """
            SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname='public' AND c.relname=ANY(@tables) AND c.relkind IN ('r','p')
              AND (has_table_privilege(@account,c.oid,'TRUNCATE') OR has_table_privilege(@account,c.oid,'TRIGGER')
                OR has_table_privilege(@account,c.oid,'REFERENCES')
                OR has_table_privilege(@account,c.oid,'SELECT WITH GRANT OPTION')
                OR has_table_privilege(@account,c.oid,'INSERT WITH GRANT OPTION')
                OR has_table_privilege(@account,c.oid,'UPDATE WITH GRANT OPTION')
                OR has_table_privilege(@account,c.oid,'DELETE WITH GRANT OPTION')))
            """, ct, ("account", account), ("tables", Tables)))!;
        if (dangerous) throw new InvalidDataException("Le compte technique dispose de droits de gestion ou de délégation sur les tables GLPI Pilot. Choisissez un compte dédié limité aux opérations métier ; ses droits existants n’ont pas été modifiés.");
    }

    public static async Task<PostgresProvisionResult> PrepareAsync(string administrativeConnection, string database, string account, string accountPassword, CancellationToken ct = default)
    {
        AdministrationCapabilities.Require();
        if (AppPaths.SimulationRoot is not null) throw new InvalidOperationException("La démonstration ne peut pas préparer une base PostgreSQL réelle.");
        database = Name(database); account = Name(account);
        var builder = new NpgsqlConnectionStringBuilder(administrativeConnection)
        { Pooling = false, IncludeErrorDetail = false, LogParameters = false, ApplicationName = "GLPI-Pilot Administration", Timeout = 15, CommandTimeout = 60, CancellationTimeout = 2000 };
        if (string.IsNullOrEmpty(builder.Database)) builder.Database = "postgres";
        if (string.Equals(account, builder.Username, StringComparison.Ordinal)) throw new InvalidDataException("Utilisez un compte technique distinct du compte administrateur pour les agents.");
        bool created = false, accountCreated = false;
        await using (var maintenance = new NpgsqlConnection(builder.ToString()))
        {
            await maintenance.OpenAsync(ct);
            var exists = (bool)(await Scalar(maintenance, "SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@name)", ct, ("name", account)))!;
            if (exists)
            {
                var elevated = (bool)(await Scalar(maintenance, "SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR NOT rolcanlogin OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid) FROM pg_roles WHERE rolname=@name", ct, ("name", account)))!;
                if (elevated) throw new InvalidDataException("Le compte technique existe avec des privilèges d’administration, des appartenances de rôle ou sans connexion. Choisissez un compte dédié aux agents ; ses droits existants n’ont pas été modifiés.");
            }
            else if (string.IsNullOrEmpty(accountPassword) || accountPassword.Length > 8000 || accountPassword.IndexOf('\0') >= 0)
                throw new InvalidDataException("Un mot de passe est nécessaire pour créer le compte technique des agents.");
            if (!(bool)(await Scalar(maintenance, "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@name)", ct, ("name", database)))!)
            {
                // CREATE DATABASE is deliberately outside a transaction. The
                // validated identifier is quoted; a parallel creation is harmless.
                try { await Execute(maintenance, "CREATE DATABASE " + Quote(database) + " TEMPLATE template0 ENCODING 'UTF8'", ct); created = true; }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DuplicateDatabase) { }
            }
            if (!exists)
            {
                var sql = (string)(await Scalar(maintenance, "SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L',@name::text,@secret::text)", ct, ("name", account), ("secret", accountPassword)))!;
                try { await Execute(maintenance, sql, ct); accountCreated = true; }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DuplicateObject)
                { throw new InvalidOperationException("Le compte a été créé par une autre opération. Relancez la préparation pour vérifier ses droits."); }
            }
        }
        builder.Database = database; builder.SearchPath = "public";
        var connection = builder.ToString();
        await using (var db = new NpgsqlConnection(connection))
        {
            await db.OpenAsync(ct); var quotedAccount = Quote(account);
            // Only a newly created, dedicated database may have its PUBLIC
            // default tightened. Existing databases are inspected without revokes.
            if (created) await Execute(db, "REVOKE CREATE ON SCHEMA public FROM PUBLIC", ct);
            await EnsureAgentRightsAsync(db, account, ct);
            // Version checks are performed by both stores before their transaction
            // commits. Existing business rows and central identities are retained.
            await new TicketLibraryPostgresStore(connection).InitializeAsync(ct);
            await new PostgresTeamWorkspaceReplica(connection).InitializeAsync(ct);
            await Execute(db, "GRANT CONNECT ON DATABASE " + Quote(database) + " TO " + quotedAccount, ct);
            await Execute(db, "GRANT USAGE ON SCHEMA public TO " + quotedAccount, ct);
            foreach (var table in Tables)
                await Execute(db, "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public." + Quote(table) + " TO " + quotedAccount, ct);
            await Execute(db, "GRANT USAGE, SELECT ON SEQUENCE public.gp_shared_changes_sequence_seq TO " + quotedAccount, ct);
            // An administrator's custom default privileges must not accidentally
            // turn a newly created table into an administrative power for agents.
            await EnsureAgentRightsAsync(db, account, ct);
        }
        // Only a successful complete preparation is reported as ready. On a
        // later permission error, created objects are preserved for a safe retry.
        return new(created, accountCreated, database, account, created
            ? "Base créée, tables préparées et accès du compte technique configuré. Les identifiants administrateur ne sont pas conservés."
            : "Base existante conservée, tables vérifiées et accès du compte technique configuré. Aucun ticket ni élément d’équipe n’a été effacé.");
    }
}
