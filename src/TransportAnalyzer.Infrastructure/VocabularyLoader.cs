using System.Globalization;
using Npgsql;

namespace TransportAnalyzer.Infrastructure;

public static class VocabularyLoader
{
    private const string VocabularyHeader = "attribute,code,label,description,parent_code";
    private const string MappingHeader = "attribute,source_column,source_value,code,priority";
    private const int Columns = 5;

    internal sealed record VocabularyRow(string Attribute, string Code, string Label, string Description, string? ParentCode);
    internal sealed record MappingRow(string SourceId, string Attribute, string SourceColumn, string SourceValue, string Code, int Priority);
    internal sealed record VocabularyData(IReadOnlyList<VocabularyRow> Vocabulary, IReadOnlyList<MappingRow> Mappings);

    /// <summary>Replaces <c>vocabulary</c> and <c>vocabulary_map</c> with the files under <paramref name="dataRoot"/>.
    /// The files are read and validated before the database is touched.</summary>
    /// <exception cref="InvalidDataException">A file is missing, empty or malformed, or references an unknown code;
    /// the message names the file and line.</exception>
    public static async Task LoadAsync(NpgsqlConnection conn, string dataRoot, CancellationToken ct)
    {
        var data = Read(dataRoot);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("TRUNCATE vocabulary, vocabulary_map", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        foreach (var r in data.Vocabulary)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO vocabulary (attribute, code, label, description, parent_code) VALUES ($1,$2,$3,$4,$5)", conn, tx);
            cmd.Parameters.AddWithValue(r.Attribute);
            cmd.Parameters.AddWithValue(r.Code);
            cmd.Parameters.AddWithValue(r.Label);
            cmd.Parameters.AddWithValue(r.Description);
            cmd.Parameters.AddWithValue((object?)r.ParentCode ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var r in data.Mappings)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO vocabulary_map (source_id, attribute, source_column, source_value, code, priority) VALUES ($1,$2,$3,$4,$5,$6)", conn, tx);
            cmd.Parameters.AddWithValue(r.SourceId);
            cmd.Parameters.AddWithValue(r.Attribute);
            cmd.Parameters.AddWithValue(r.SourceColumn);
            cmd.Parameters.AddWithValue(r.SourceValue);
            cmd.Parameters.AddWithValue(r.Code);
            cmd.Parameters.AddWithValue(r.Priority);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Reads and validates <c>vocabulary.csv</c> and every <c>mappings/*.csv</c> whose header is the
    /// vocabulary-mapping header (others are skipped).</summary>
    internal static VocabularyData Read(string dataRoot)
    {
        var vocabFile = Path.Combine(dataRoot, "vocabulary.csv");
        if (!File.Exists(vocabFile)) throw new InvalidDataException($"{vocabFile}: file not found.");
        var vocabRows = ReadCsv(vocabFile);
        if (vocabRows.Count == 0)
            throw new InvalidDataException($"{vocabFile}: file is empty; expected header '{VocabularyHeader}'.");
        RequireHeader(vocabFile, vocabRows[0], VocabularyHeader);

        var vocabulary = new List<VocabularyRow>();
        var codes = new HashSet<(string Attribute, string Code)>();
        foreach (var (line, f) in vocabRows.Skip(1))
        {
            RequireColumns(vocabFile, line, f, VocabularyHeader);
            var row = new VocabularyRow(f[0].Trim(), f[1].Trim(), f[2], f[3], f[4].Trim() is { Length: > 0 } p ? p : null);
            if (row.Attribute.Length == 0 || row.Code.Length == 0)
                throw new InvalidDataException($"{vocabFile} line {line}: attribute and code must not be empty.");
            if (!codes.Add((row.Attribute, row.Code)))
                throw new InvalidDataException(
                    $"{vocabFile} line {line}: code '{row.Code}' for attribute '{row.Attribute}' appears more than once.");
            vocabulary.Add(row);
        }
        foreach (var ((line, _), row) in vocabRows.Skip(1).Zip(vocabulary))
            if (row.ParentCode is { } parent && !codes.Contains((row.Attribute, parent)))
                throw new InvalidDataException(
                    $"{vocabFile} line {line}: parent_code '{parent}' for attribute '{row.Attribute}' is not a code of that attribute.");

        var mappingsDir = Path.Combine(dataRoot, "mappings");
        var mapFiles = Directory.Exists(mappingsDir)
            ? Directory.GetFiles(mappingsDir, "*.csv").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
        var mappings = new List<MappingRow>();
        foreach (var file in mapFiles)
        {
            var rows = ReadCsv(file);
            if (rows.Count == 0 || string.Join(",", rows[0].Fields) != MappingHeader) continue;
            var sourceId = Path.GetFileNameWithoutExtension(file);
            foreach (var (line, f) in rows.Skip(1))
            {
                RequireColumns(file, line, f, MappingHeader);
                if (!int.TryParse(f[4].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var priority))
                    throw new InvalidDataException($"{file} line {line}: priority '{f[4]}' is not an integer.");
                var row = new MappingRow(sourceId, f[0].Trim(), f[1].Trim(), f[2].Trim(), f[3].Trim(), priority);
                if (!codes.Contains((row.Attribute, row.Code)))
                    throw new InvalidDataException(
                        $"{file} line {line}: code '{row.Code}' for attribute '{row.Attribute}' is not in {vocabFile}.");
                mappings.Add(row);
            }
        }
        return new VocabularyData(vocabulary, mappings);
    }

    private static void RequireHeader(string file, (int Line, List<string> Fields) row, string header)
    {
        var actual = string.Join(",", row.Fields);
        if (actual != header)
            throw new InvalidDataException($"{file} line {row.Line}: expected header '{header}', found '{actual}'.");
    }

    private static void RequireColumns(string file, int line, List<string> fields, string header)
    {
        if (fields.Count != Columns)
            throw new InvalidDataException(
                $"{file} line {line}: expected {Columns} columns ({header}), found {fields.Count}.");
    }

    /// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, embedded commas/newlines. Each record carries
    /// the 1-based physical line it starts on; blank lines are skipped.</summary>
    /// <exception cref="InvalidDataException">A quoted field is not closed.</exception>
    internal static List<(int Line, List<string> Fields)> ReadCsv(string path)
    {
        var text = File.ReadAllText(path);
        var rows = new List<(int, List<string>)>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var line = 1;
        var rowLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else
                {
                    if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) line++;
                    field.Append(c);
                }
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add((rowLine, row));
                row = [];
                line++;
                rowLine = line;
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException($"{path} line {rowLine}: unterminated quoted field.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add((rowLine, row));
        }
        return rows;
    }
}
