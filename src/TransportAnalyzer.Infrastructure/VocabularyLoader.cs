using System.Globalization;
using Npgsql;

namespace TransportAnalyzer.Infrastructure;

public static class VocabularyLoader
{
    private const string MappingHeader = "attribute,source_column,source_value,code,priority";

    public static async Task LoadAsync(NpgsqlConnection conn, string dataRoot, CancellationToken ct)
    {
        var vocabFile = Path.Combine(dataRoot, "vocabulary.csv");
        var vocab = ReadCsv(vocabFile);
        var mappingsDir = Path.Combine(dataRoot, "mappings");
        var mapFiles = Directory.Exists(mappingsDir)
            ? Directory.GetFiles(mappingsDir, "*.csv").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("TRUNCATE vocabulary, vocabulary_map", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        foreach (var r in vocab.Skip(1))
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO vocabulary (attribute, code, label, description, parent_code) VALUES ($1,$2,$3,$4,$5)", conn, tx);
            cmd.Parameters.AddWithValue(r[0]);
            cmd.Parameters.AddWithValue(r[1]);
            cmd.Parameters.AddWithValue(r[2]);
            cmd.Parameters.AddWithValue(r[3]);
            cmd.Parameters.AddWithValue(r.Count > 4 && r[4].Length > 0 ? r[4] : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var file in mapFiles)
        {
            var rows = ReadCsv(file);
            if (rows.Count == 0 || string.Join(",", rows[0]) != MappingHeader) continue;
            var sourceId = Path.GetFileNameWithoutExtension(file);
            foreach (var r in rows.Skip(1))
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO vocabulary_map (source_id, attribute, source_column, source_value, code, priority) VALUES ($1,$2,$3,$4,$5,$6)", conn, tx);
                cmd.Parameters.AddWithValue(sourceId);
                cmd.Parameters.AddWithValue(r[0]);
                cmd.Parameters.AddWithValue(r[1]);
                cmd.Parameters.AddWithValue(r[2]);
                cmd.Parameters.AddWithValue(r[3]);
                cmd.Parameters.AddWithValue(int.Parse(r[4], CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, embedded commas/newlines.</summary>
    internal static List<List<string>> ReadCsv(string path)
    {
        var text = File.ReadAllText(path);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
