using Npgsql;
using TransportAnalyzer.Infrastructure;

namespace TransportAnalyzer.Tests;

/// <summary>Load-time validation of vocabulary.csv and the mapping files. The connection is never opened: every
/// file problem must be reported before the loader touches the database.</summary>
public class VocabularyLoaderTests : IDisposable
{
    private const string Header = "attribute,code,label,description,parent_code";
    private const string MapHeader = "attribute,source_column,source_value,code,priority";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ta-vocab-" + Guid.NewGuid().ToString("N"));

    public VocabularyLoaderTests() => Directory.CreateDirectory(Path.Combine(_root, "mappings"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string VocabPath => Path.Combine(_root, "vocabulary.csv");
    private string MapPath => Path.Combine(_root, "mappings", "src.csv");

    private void Vocab(params string[] lines) => File.WriteAllText(VocabPath, string.Join("\n", lines) + "\n");
    private void Map(params string[] lines) => File.WriteAllText(MapPath, string.Join("\n", lines) + "\n");

    private const string Car = "vehicle_class,car,Car,\"A car, of any kind.\",";
    private const string Truck = "vehicle_class,truck,Truck,A truck.,";

    private async Task<InvalidDataException> LoadFails()
    {
        await using var conn = new NpgsqlConnection("Host=unused.invalid");
        return await Assert.ThrowsAsync<InvalidDataException>(() =>
            VocabularyLoader.LoadAsync(conn, _root, CancellationToken.None));
    }

    [Fact]
    public async Task MissingVocabulary_NamesFile()
    {
        var ex = await LoadFails();
        Assert.Contains(VocabPath, ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task EmptyVocabulary_NamesFileAndExpectedHeader()
    {
        File.WriteAllText(VocabPath, "");
        var ex = await LoadFails();
        Assert.Contains(VocabPath, ex.Message);
        Assert.Contains("empty", ex.Message);
        Assert.Contains(Header, ex.Message);
    }

    [Fact]
    public async Task WrongVocabularyHeader_NamesLineOne()
    {
        Vocab("attribute,code,label", Car);
        var ex = await LoadFails();
        Assert.Contains($"{VocabPath} line 1", ex.Message);
        Assert.Contains(Header, ex.Message);
    }

    [Fact]
    public async Task ShortVocabularyRow_NamesLineAndColumnCount()
    {
        // The quoted description spans two physical lines; the short row is on line 4.
        Vocab(Header, "vehicle_class,car,Car,\"A car,\nof any kind.\",", "vehicle_class,truck,Truck");
        var ex = await LoadFails();
        Assert.Contains($"{VocabPath} line 4", ex.Message);
        Assert.Contains("expected 5 columns", ex.Message);
        Assert.Contains("found 3", ex.Message);
    }

    [Fact]
    public async Task UnterminatedQuote_NamesLine()
    {
        Vocab(Header, Car, "vehicle_class,truck,Truck,\"A truck.,");
        var ex = await LoadFails();
        Assert.Contains($"{VocabPath} line 3", ex.Message);
        Assert.Contains("unterminated", ex.Message);
    }

    [Fact]
    public async Task UnknownParentCode_NamesLineAttributeAndCode()
    {
        Vocab(Header, Car, "vehicle_class,pickup,Pickup,A pickup.,light_duty");
        var ex = await LoadFails();
        Assert.Contains($"{VocabPath} line 3", ex.Message);
        Assert.Contains("parent_code 'light_duty'", ex.Message);
        Assert.Contains("'vehicle_class'", ex.Message);
    }

    [Fact]
    public async Task ParentCodeOfAnotherAttribute_IsRejected()
    {
        Vocab(Header, Car, "operator_type,private,Private,Private.,car");
        var ex = await LoadFails();
        Assert.Contains($"{VocabPath} line 3", ex.Message);
        Assert.Contains("parent_code 'car'", ex.Message);
        Assert.Contains("'operator_type'", ex.Message);
    }

    [Fact]
    public async Task MappingCodeNotInVocabulary_NamesFileLineAttributeAndCode()
    {
        Vocab(Header, Car, Truck);
        Map(MapHeader, "vehicle_class,body_typ,1,car,1", "vehicle_class,body_typ,2,bus,1");
        var ex = await LoadFails();
        Assert.Contains($"{MapPath} line 3", ex.Message);
        Assert.Contains("code 'bus'", ex.Message);
        Assert.Contains("'vehicle_class'", ex.Message);
    }

    [Fact]
    public async Task ShortMappingRow_NamesLineAndColumnCount()
    {
        Vocab(Header, Car);
        Map(MapHeader, "vehicle_class,body_typ,1,car");
        var ex = await LoadFails();
        Assert.Contains($"{MapPath} line 2", ex.Message);
        Assert.Contains("expected 5 columns", ex.Message);
        Assert.Contains("found 4", ex.Message);
    }

    [Fact]
    public async Task NonIntegerPriority_NamesLine()
    {
        Vocab(Header, Car);
        Map(MapHeader, "vehicle_class,body_typ,1,car,first");
        var ex = await LoadFails();
        Assert.Contains($"{MapPath} line 2", ex.Message);
        Assert.Contains("priority 'first'", ex.Message);
    }

    [Fact]
    public async Task MappingFileWithOtherHeader_IsSkipped()
    {
        // e.g. the BTS row-label mappings: not vocabulary mappings, never validated here.
        Vocab(Header, Car);
        File.WriteAllText(Path.Combine(_root, "mappings", "bts.csv"), "row_label,mode\nx\n");
        await using var conn = new NpgsqlConnection("Host=unused.invalid");
        // Validation passes, so the loader goes on to the (unopened) connection.
        var ex = await Record.ExceptionAsync(() => VocabularyLoader.LoadAsync(conn, _root, CancellationToken.None));
        Assert.IsNotType<InvalidDataException>(ex);
    }

    [Fact]
    public void RealDataFiles_AreValid() =>
        VocabularyLoader.Read(Path.Combine(AppContext.BaseDirectory, "data"));
}
