namespace Core.Persistence.Repositories;

public abstract class Entity<TId>(TId id) : IEntity<TId>, IEntityTimestamps
{
    public TId Id { get; set; } = id;
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public DateTime? DeletedDate { get; set; }

    public Entity() : this(default!)
    {
    }
}
