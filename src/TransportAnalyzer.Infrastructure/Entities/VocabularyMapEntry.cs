namespace TransportAnalyzer.Infrastructure.Entities;

public class VocabularyMapEntry
{
    public string SourceId { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string SourceColumn { get; set; } = "";
    public string SourceValue { get; set; } = "";
    public string Code { get; set; } = "";
    public int Priority { get; set; }
}
