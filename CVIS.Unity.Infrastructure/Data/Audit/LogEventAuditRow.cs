namespace CVIS.Unity.Infrastructure.Data.Audit;

public sealed class LogEventAuditRow
{
    public long Id { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
