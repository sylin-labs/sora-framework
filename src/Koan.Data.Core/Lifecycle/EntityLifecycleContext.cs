using System.Linq.Expressions;
using System.Reflection;

namespace Koan.Data.Core.Lifecycle;

/// <summary>Runtime state shared by the handlers for one entity persistence operation.</summary>
public sealed class EntityLifecycleContext<TEntity> where TEntity : class
{
    private static readonly Lazy<IReadOnlyDictionary<string, Func<TEntity, object?>>> PropertyGetters =
        new(CreatePropertyGetterMap);

    private readonly Dictionary<string, object?> _protectedSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _items = new(StringComparer.Ordinal);

    internal EntityLifecycleContext(
        TEntity current,
        EntityLifecycleOperation operation,
        TEntity? prior,
        CancellationToken cancellationToken)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Operation = operation;
        Prior = prior;
        CancellationToken = cancellationToken;
    }

    public TEntity Current { get; internal set; }
    public EntityLifecycleOperation Operation { get; }
    /// <summary>The visible persisted value captured before this operation, or null for an insert-only
    /// attempt or a missing visible row. Null is not proof that the physical identity is absent.</summary>
    public TEntity? Prior { get; }
    public CancellationToken CancellationToken { get; }
    public IDictionary<string, object?> Items => _items;

    /// <summary>Protects a readable property from mutation by this and subsequent handlers.</summary>
    public void Protect(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name must be provided.", nameof(propertyName));
        if (!PropertyGetters.Value.TryGetValue(propertyName, out var getter))
            throw new InvalidOperationException(
                $"Property '{propertyName}' does not exist on {typeof(TEntity).Name} or is not readable.");
        _protectedSnapshots[propertyName] = getter(Current);
    }

    /// <summary>Protects every public readable property from mutation.</summary>
    public void ProtectAll()
    {
        foreach (var (name, getter) in PropertyGetters.Value)
            _protectedSnapshots[name] = getter(Current);
    }

    public void AllowMutation(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name must be provided.", nameof(propertyName));
        _protectedSnapshots.Remove(propertyName);
    }

    public EntityLifecycleResult Proceed() => EntityLifecycleResult.Proceed();
    public EntityLifecycleResult Cancel(string reason, string? code = null) =>
        EntityLifecycleResult.Cancel(reason, code);

    internal void ValidateProtection()
    {
        foreach (var (name, snapshot) in _protectedSnapshots)
        {
            if (PropertyGetters.Value.TryGetValue(name, out var getter) && !Equals(getter(Current), snapshot))
                throw new InvalidOperationException(
                    $"Entity field '{name}' is protected and cannot be mutated during lifecycle execution.");
        }
    }

    internal void UpdateCurrent(TEntity current) =>
        Current = current ?? throw new ArgumentNullException(nameof(current));

    private static IReadOnlyDictionary<string, Func<TEntity, object?>> CreatePropertyGetterMap()
    {
        var result = new Dictionary<string, Func<TEntity, object?>>(StringComparer.Ordinal);
        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead))
        {
            var instance = Expression.Parameter(typeof(TEntity), "instance");
            var value = Expression.Convert(Expression.Property(instance, property), typeof(object));
            result[property.Name] = Expression.Lambda<Func<TEntity, object?>>(value, instance).Compile();
        }
        return result;
    }
}
