namespace TransportAnalyzer.Infrastructure.Entities;

public class VocabularyEntry
{
    public string Attribute { get; set; } = "";
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ParentCode { get; set; }
}
