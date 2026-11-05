using System.Text;
using Npgsql;
using TransportAnalyzer.Infrastructure;

namespace TransportAnalyzer.Tests;

[Collection("db")]
public class CsvStagerTests(PostgresFixture db)
{
    private static string NewSchema() => "stg_t" + Guid.NewGuid().ToString("N")[..12];

    private static MemoryStream Utf8(string s) => new(Encoding.UTF8.GetBytes(s));

    private static Dictionary<string, string> Year(string y) => new() { ["data_year"] = y };

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<List<string?[]>> RowsAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await r.ReadAsync())
        {
            var row = new string?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetString(i);
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public async Task Stage_CreatesTextTableAndCopiesRows()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        var n = await CsvStager.StageAsync(conn, s, "person", Utf8("ST_CASE,FATALS\n10001,1\n10002,2\n"), Year("2022"), "data_year", default);
        Assert.Equal(2, n);
        var cols = await RowsAsync(conn,
            $"select column_name, data_type from information_schema.columns where table_schema='{s}' and table_name='person' order by ordinal_position");
        Assert.Equal(new[] { "st_case", "fatals", "data_year" }, cols.Select(c => c[0]));
        Assert.All(cols, c => Assert.Equal("text", c[1]));
        var rows = await RowsAsync(conn, $"select st_case, fatals, data_year from {s}.person order by st_case");
        Assert.Equal(new[] { "10001", "1", "2022" }, rows[0]);
        Assert.Equal(new[] { "10002", "2", "2022" }, rows[1]);
    }

    [Fact]
    public async Task Stage_UppercaseFileAndHeaders_Lowercased()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        await CsvStager.StageAsync(conn, s, "Vehicle", Utf8("ST_CASE,Veh_No\n1,2\n"), Year("2022"), "data_year", default);
        var cols = await RowsAsync(conn,
            $"select column_name from information_schema.columns where table_schema='{s}' and table_name='vehicle' order by ordinal_position");
        Assert.Equal(new[] { "st_case", "veh_no", "data_year" }, cols.Select(c => c[0]));
    }

    [Fact]
    public async Task Stage_Windows1252Bytes_Decoded()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        var bytes = new byte[] { (byte)'C', (byte)'I', (byte)'T', (byte)'Y', (byte)'\n', (byte)'S', (byte)'A', (byte)'N', (byte)' ', (byte)'J', (byte)'O', (byte)'S', 0xC9, (byte)'\n' };
        await CsvStager.StageAsync(conn, s, "acc", new MemoryStream(bytes), Year("2022"), "data_year", default);
        var rows = await RowsAsync(conn, $"select city from {s}.acc");
        Assert.Equal("SAN JOSÉ", rows[0][0]);
    }

    [Fact]
    public async Task Stage_Utf8Bom_Stripped()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("A,B\n1,2\n")).ToArray();
        await CsvStager.StageAsync(conn, s, "t", new MemoryStream(bytes), Year("2022"), "data_year", default);
        var cols = await RowsAsync(conn,
            $"select column_name from information_schema.columns where table_schema='{s}' and table_name='t' order by ordinal_position");
        Assert.Equal(new[] { "a", "b", "data_year" }, cols.Select(c => c[0]));
    }

    [Fact]
    public async Task Stage_NewColumnInLaterYear_AddsColumn()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        await CsvStager.StageAsync(conn, s, "t", Utf8("a\n1\n"), Year("2021"), "data_year", default);
        await CsvStager.StageAsync(conn, s, "t", Utf8("a,b\n2,x\n"), Year("2022"), "data_year", default);
        var rows = await RowsAsync(conn, $"select data_year, a, b from {s}.t order by data_year");
        Assert.Equal(new string?[] { "2021", "1", null }, rows[0]);
        Assert.Equal(new string?[] { "2022", "2", "x" }, rows[1]);
    }

    [Fact]
    public async Task Stage_SameYearTwice_ReplacesRows()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        await CsvStager.StageAsync(conn, s, "t", Utf8("a\n1\n2\n"), Year("2022"), "data_year", default);
        await CsvStager.StageAsync(conn, s, "t", Utf8("a\n1\n2\n"), Year("2022"), "data_year", default);
        await CsvStager.StageAsync(conn, s, "t", Utf8("a\n9\n"), Year("2021"), "data_year", default);
        var rows = await RowsAsync(conn, $"select count(*)::text from {s}.t where data_year='2022'");
        Assert.Equal("2", rows[0][0]);
    }

    [Fact]
    public async Task Stage_InvalidTableName_Throws()
    {
        await using var conn = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CsvStager.StageAsync(conn, NewSchema(), "x;drop", Utf8("a\n1\n"), Year("2022"), "data_year", default));
    }

    [Fact]
    public async Task Stage_InvalidSchemaName_Throws()
    {
        await using var conn = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CsvStager.StageAsync(conn, "1bad", "t", Utf8("a\n1\n"), Year("2022"), "data_year", default));
    }

    [Fact]
    public async Task Stage_ReplaceColumnNotInExtraColumns_Throws()
    {
        await using var conn = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CsvStager.StageAsync(conn, NewSchema(), "t", Utf8("a\n1\n"), Year("2022"), "other", default));
    }

    [Fact]
    public async Task Stage_DuplicateNormalizedHeaders_Throws()
    {
        await using var conn = await OpenAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            CsvStager.StageAsync(conn, NewSchema(), "t", Utf8("Veh No,VEH_NO\n1,2\n"), Year("2022"), "data_year", default));
        Assert.Contains("Veh No", ex.Message);
        Assert.Contains("VEH_NO", ex.Message);
    }

    [Fact]
    public async Task Stage_ShortRowGetsNull_EmptyFieldStaysEmpty_QuotedCommaKept()
    {
        var s = NewSchema();
        await using var conn = await OpenAsync();
        await CsvStager.StageAsync(conn, s, "t", Utf8("a,b,c\n1,,\"x,y\"\n2\n"), Year("2022"), "data_year", default);
        var rows = await RowsAsync(conn, $"select a, b, c from {s}.t order by a");
        Assert.Equal(new string?[] { "1", "", "x,y" }, rows[0]);
        Assert.Equal(new string?[] { "2", null, null }, rows[1]);
    }

    [Fact]
    public async Task Stage_LongRow_ThrowsWithLineNumber()
    {
        await using var conn = await OpenAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CsvStager.StageAsync(conn, NewSchema(), "t", Utf8("a,b\n1,2\n3,4,5\n"), Year("2022"), "data_year", default));
        Assert.Contains("3", ex.Message);
    }
}
