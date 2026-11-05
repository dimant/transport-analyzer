using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace TransportAnalyzer.Infrastructure;

/// <summary>
/// Stages a CSV into a schema.table of text columns. The caller owns the connection and transaction.
/// </summary>
public static partial class CsvStager
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex NonIdentifierChars();

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static CsvStager() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<long> StageAsync(NpgsqlConnection conn, string schema, string table, Stream csv,
        IReadOnlyDictionary<string, string> extraColumns, string replaceWhereColumn, CancellationToken ct)
    {
        schema = Identifier(schema, "schema");
        table = Identifier(table, "table");
        var extras = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in extraColumns)
            extras.Add(new(Identifier(name, "extra column"), value));
        replaceWhereColumn = Identifier(replaceWhereColumn, "replace column");
        var replaceValue = extras.FirstOrDefault(e => e.Key == replaceWhereColumn).Value
            ?? throw new ArgumentException($"Replace column '{replaceWhereColumn}' must be a key of extraColumns.", nameof(replaceWhereColumn));

        using var reader = new StringReader(await DecodeAsync(csv, ct));
        using var parser = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
        });

        if (!await parser.ReadAsync())
            throw new ArgumentException("CSV is empty: no header row.", nameof(csv));
        var rawHeader = Enumerable.Range(0, parser.Parser.Count).Select(i => parser.GetField(i) ?? "").ToArray();
        var header = new string[rawHeader.Length];
        var seen = new Dictionary<string, string>();
        for (var i = 0; i < rawHeader.Length; i++)
        {
            var name = NonIdentifierChars().Replace(rawHeader[i].Trim().ToLowerInvariant(), "_");
            if (name.Length == 0)
                throw new ArgumentException($"Header column {i + 1} is empty.", nameof(csv));
            if (seen.TryGetValue(name, out var first))
                throw new ArgumentException($"Header columns '{first}' and '{rawHeader[i]}' both normalize to '{name}'.", nameof(csv));
            if (extras.Any(e => e.Key == name))
                throw new ArgumentException($"Header column '{rawHeader[i]}' collides with extra column '{name}'.", nameof(csv));
            seen[name] = rawHeader[i];
            header[i] = name;
        }

        var allColumns = header.Concat(extras.Select(e => e.Key)).ToArray();
        var ddl = new StringBuilder($"create schema if not exists {Q(schema)};\n");
        ddl.Append($"create table if not exists {Q(schema)}.{Q(table)} ({string.Join(", ", allColumns.Select(c => Q(c) + " text"))});\n");
        foreach (var c in allColumns)
            ddl.Append($"alter table {Q(schema)}.{Q(table)} add column if not exists {Q(c)} text;\n");
        await using (var cmd = new NpgsqlCommand(ddl.ToString(), conn))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var del = new NpgsqlCommand($"delete from {Q(schema)}.{Q(table)} where {Q(replaceWhereColumn)} = @value", conn))
        {
            del.Parameters.AddWithValue("value", replaceValue);
            await del.ExecuteNonQueryAsync(ct);
        }

        long count = 0;
        await using var importer = await conn.BeginBinaryImportAsync(
            $"copy {Q(schema)}.{Q(table)} ({string.Join(", ", allColumns.Select(Q))}) from stdin (format binary)", ct);
        while (await parser.ReadAsync())
        {
            var n = parser.Parser.Count;
            if (n > header.Length)
                throw new InvalidDataException($"CSV line {parser.Parser.Row} has {n} fields but the header has {header.Length}.");
            await importer.StartRowAsync(ct);
            for (var i = 0; i < header.Length; i++)
            {
                if (i < n) await importer.WriteAsync(parser.GetField(i) ?? "", NpgsqlDbType.Text, ct);
                else await importer.WriteNullAsync(ct);
            }
            foreach (var e in extras)
                await importer.WriteAsync(e.Value, NpgsqlDbType.Text, ct);
            count++;
        }
        await importer.CompleteAsync(ct);
        return count;
    }

    private static string Identifier(string name, string what)
    {
        var lowered = name?.ToLowerInvariant() ?? "";
        if (!IdentifierPattern().IsMatch(lowered))
            throw new ArgumentException($"Invalid {what} name '{name}'.", nameof(name));
        return lowered;
    }

    private static string Q(string identifier) => "\"" + identifier + "\"";

    private static async Task<string> DecodeAsync(Stream csv, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await csv.CopyToAsync(buffer, ct);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(1252).GetString(bytes); }
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
