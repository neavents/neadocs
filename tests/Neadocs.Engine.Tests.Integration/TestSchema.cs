namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Npgsql;

/// <summary>
/// Names throwaway schemas so their age is readable, and clears out the ones previous runs left
/// behind.
/// </summary>
/// <remarks>
/// <para>
/// Every host here already dropped its own schema on disposal, and thirty-three of them had still
/// accumulated in the shared database. Disposal is best-effort by construction: a run that is
/// cancelled, times out, crashes, or throws while a fixture is being built never reaches it. So
/// cleanup cannot only be something the run does on its way out — it has to be something the next
/// run does on its way in.
/// </para>
/// <para>
/// <b>What the debris actually cost.</b> Thirteen of those schemas held HNSW indexes bound to a
/// <c>vector</c> extension OID that no longer existed, because the extension had been dropped and
/// recreated since. <c>pg_dump</c> reads the catalogue for every table it dumps, reached those
/// rows, and failed with <c>cache lookup failed for access method 212325</c> — for the entire
/// database, not just the test schemas. The estate's largest database could not be backed up at
/// all, and nothing had noticed because nothing had ever tried.
/// </para>
/// <para>
/// <b>Why the timestamp is in the name.</b> PostgreSQL does not record when a schema was created,
/// so a sweeper has no way to tell a five-week-old corpse from a schema another process is using
/// right now. Dropping everything that matches would destroy a concurrent run's fixture. Putting
/// the creation time in the name makes age a fact the sweeper can read, so it can leave anything
/// recent alone and still guarantee that debris does not survive the day.
/// </para>
/// </remarks>
internal static partial class TestSchema
{
    /// <summary>The only shape this class will ever drop: a known prefix, a timestamp, a suffix.</summary>
    [GeneratedRegex(@"^neadocs_(guard|test|vec)_\d{8}t\d{6}_[0-9a-f]{8}$")]
    private static partial Regex TimestampedName();

    /// <summary>
    /// The naming this class replaced: a bare hex suffix, with no way to tell how old it is.
    /// </summary>
    [GeneratedRegex(@"^neadocs_(guard|test|vec)_[0-9a-f]{10,12}$")]
    private static partial Regex LegacyName();

    /// <summary>How old a schema must be before a later run is willing to drop it.</summary>
    /// <remarks>
    /// Longer than any plausible run of this suite, so a sweep can never take a schema out from
    /// under a test that is still using it — including a second process running the suite
    /// concurrently, which is the case a simple "drop everything that matches" would corrupt.
    /// </remarks>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    // Lowercase separator, not "T". DocumentEngineOptionsValidator requires a schema to match
    // [a-z_][a-z0-9_]{0,62}, and it rejected the first version of this with a clear message before
    // a single test touched the database — which is the validator doing exactly its job.
    private const string TimestampFormat = "yyyyMMdd't'HHmmss";

    /// <summary>
    /// A unique schema name carrying its own creation time, e.g. <c>neadocs_test_20260823t013055_a1b2c3d4</c>.
    /// </summary>
    public static string Name(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        string stamp = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        string unique = Guid.NewGuid().ToString("N")[..8];

        return $"{prefix}{stamp}_{unique}";
    }

    /// <summary>
    /// Drops throwaway schemas left by earlier runs — all three prefixes, not just the caller's.
    /// </summary>
    /// <remarks>
    /// Every prefix, deliberately. When each host swept only its own, a run in an environment
    /// where the vector and guard suites are skipped cleaned up seven schemas and left
    /// twenty-four, because a fixture that never runs never sweeps. Whichever fixture starts
    /// first should leave the database clean, so the one that runs everywhere does the work for
    /// the ones that do not.
    /// <para>
    /// Never throws: a sweep that fails must not fail the suite. Leftover schemas are a hygiene
    /// problem; an unrunnable suite is a worse one.
    /// </para>
    /// </summary>
    public static async Task SweepStaleAsync(string connectionString)
    {
        try
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();

            // Two steps rather than a DO block. Npgsql substitutes @parameters into the SQL text
            // before sending, and a dollar-quoted procedure body is a string literal — the
            // placeholders inside one are never replaced, so the block would run against the
            // literal text "@prefix" and silently sweep nothing.
            List<string> stale = [];

            await using (NpgsqlCommand find = connection.CreateCommand())
            {
                // Two populations, and they are swept on different rules.
                //
                // Timestamped names carry their age, so only the ones older than StaleAfter go —
                // which is what makes a concurrent run of this suite safe.
                //
                // Legacy names have no age to read. They can only have been created before this
                // naming existed, because nothing produces that shape any more, so every one of
                // them is debris by definition and goes unconditionally. Thirty-three were waiting
                // when this was written. The narrow risk is someone running an OLD checkout of
                // this suite at the same moment as a new one; the cost there is a failed test, not
                // lost data, and it stops being possible once the old commit is gone.
                find.CommandText = """
                    SELECT nspname FROM pg_namespace
                    WHERE nspname ~ '^neadocs_(guard|test|vec)_'
                      AND (
                        (    substring(nspname from '(\d{8}t\d{6})_') IS NOT NULL
                         AND to_timestamp(substring(nspname from '(\d{8}t\d{6})_'), 'YYYYMMDD"t"HH24MISS')
                             < (now() at time zone 'utc') - @stale::interval)
                        OR nspname ~ '^neadocs_(guard|test|vec)_[0-9a-f]{10,12}$'
                      )
                    """;
                find.Parameters.AddWithValue("stale", $"{StaleAfter.TotalHours} hours");

                await using NpgsqlDataReader reader = await find.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    stale.Add(reader.GetString(0));
                }
            }

            int dropped = 0;
            int undroppable = 0;

            foreach (string schema in stale)
            {
                // An identifier cannot be a parameter, so this is interpolated — and therefore
                // re-checked against the shape this method is allowed to drop. The name came from
                // pg_namespace and already matched the query above; the guard is here because a
                // DROP built by string concatenation should never be one edit away from dropping
                // something else.
                if (!TimestampedName().IsMatch(schema) && !LegacyName().IsMatch(schema))
                {
                    continue;
                }

                // A connection of its own, per schema. The first failure below raises XX000, which
                // Npgsql treats as fatal and closes the connection on — so a shared connection
                // turned one undroppable schema into "Connection is not open" for every schema
                // after it, and the sweep silently stopped after the first casualty.
                try
                {
                    await using NpgsqlConnection drops = new(connectionString);
                    await drops.OpenAsync();
                    await using NpgsqlCommand drop = drops.CreateCommand();
                    drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
                    await drop.ExecuteNonQueryAsync();
                    dropped++;
                }
                catch (PostgresException ex) when (ex.SqlState == "XX000")
                {
                    // "cache lookup failed for access method N": the schema holds an HNSW index
                    // bound to a `vector` extension OID that no longer exists, and PostgreSQL
                    // cannot drop an index whose access method it cannot look up. DROP SCHEMA
                    // CASCADE and DROP INDEX both refuse. There is no supported DDL that removes
                    // these; they go when the database is rebuilt, which the CloudNativePG cutover
                    // does. Counted, not shouted about once per schema.
                    undroppable++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TestSchema] could not drop {schema}: {ex.Message}");
                }
            }

            if (dropped > 0 || undroppable > 0)
            {
                Console.Error.WriteLine(
                    $"[TestSchema] swept {dropped} stale schema(s)"
                    + (undroppable > 0
                        ? $"; {undroppable} could not be dropped (corrupted HNSW indexes — see TestSchema)"
                        : string.Empty));
            }

        }
        catch (Exception ex)
        {
            // Connecting or listing failed. Deliberately not fatal — see the summary above — but
            // no longer silent, because a sweep that never runs looks exactly like a sweep with
            // nothing to do.
            Console.Error.WriteLine($"[TestSchema] sweep did not run: {ex.Message}");
        }
    }
}
