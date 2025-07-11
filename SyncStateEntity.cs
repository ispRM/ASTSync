using Azure;
using Azure.Data.Tables;

namespace ASTSync;

public class SyncStateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "State";
    public string RowKey { get; set; } = "LastProcessedSimulation";
    public string LastSimulationId { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
