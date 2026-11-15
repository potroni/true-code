namespace DataAccessLayer.UnitOfWork;

public sealed class IdempotencyKeyAlreadyExistsException(Exception innerException)
    : Exception("An idempotency entry already exists for this key in the current scope.", innerException)
{
}
