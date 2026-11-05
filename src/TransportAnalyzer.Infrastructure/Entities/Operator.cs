namespace TransportAnalyzer.Infrastructure.Entities;

public class Operator
{
    public Guid OperatorId { get; set; }
    public string Name { get; set; } = "";
    public string Identifiers { get; set; } = "{}";
}
