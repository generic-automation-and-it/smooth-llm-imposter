namespace SmoothLlmImposter.Domain.Credentials;

public abstract class BaseEntity
{
    protected BaseEntity()
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public void Touch(DateTime utcNow)
    {
        UpdatedAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
    }
}
