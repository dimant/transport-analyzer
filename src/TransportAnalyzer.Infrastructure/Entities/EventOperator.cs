namespace TransportAnalyzer.Infrastructure.Entities;

public class EventOperator
{
    public Guid EventId { get; set; }
    public Guid OperatorId { get; set; }
    public string Role { get; set; } = "";
}
