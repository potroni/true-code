using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class IdempotencyEntry : IEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public IdempotencyOperation Operation { get; set; }
    public byte[] KeyHash { get; set; } = null!;
    public string OriginalKey { get; set; } = null!;
    public long ClassId { get; set; }
    public int Seats { get; set; }
    public string ResponseBody { get; set; } = null!;
}

public enum IdempotencyOperation
{
    CreateEnrollment = 1,
    JoinWaitlist = 2
}
