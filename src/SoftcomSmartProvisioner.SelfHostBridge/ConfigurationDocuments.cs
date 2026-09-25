using System.Reflection;

namespace SoftcomSmartProvisioner.SelfHostBridge;

public interface IConfigurationDocument
{
    object? Get(string propertyName);
    void Set(string propertyName, object? value);
    bool HasProperty(string propertyName);
    IReadOnlyCollection<string> PropertyNames { get; }
    object RawObject { get; }
}

public sealed class ReflectionConfigurationDocument : IConfigurationDocument
{
    private readonly object _value;
    private readonly Dictionary<string, PropertyInfo> _properties;

    public ReflectionConfigurationDocument(object value)
    {
        _value = value;
        _properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.CanWrite)
            .ToDictionary(x => x.Name, StringComparer.Ordinal);
    }

    public object? Get(string propertyName) => GetProperty(propertyName).GetValue(_value);

    public void Set(string propertyName, object? value)
    {
        var property = GetProperty(propertyName);
        property.SetValue(_value, ConvertValue(value, property.PropertyType));
    }

    public bool HasProperty(string propertyName) => _properties.ContainsKey(propertyName);
    public IReadOnlyCollection<string> PropertyNames => _properties.Keys;
    public object RawObject => _value;

    private PropertyInfo GetProperty(string name) => _properties.TryGetValue(name, out var property)
        ? property
        : throw new BridgeValidationException("configuration_field_missing", $"A versão instalada do SelfHost não possui o campo esperado '{name}'.");

    private static object? ConvertValue(object? value, Type target)
    {
        if (value is null) return null;
        var effective = Nullable.GetUnderlyingType(target) ?? target;
        return effective.IsInstanceOfType(value) ? value : Convert.ChangeType(value, effective);
    }
}

public sealed class DictionaryConfigurationDocument : IConfigurationDocument
{
    private readonly IDictionary<string, object?> _values;

    public DictionaryConfigurationDocument(IDictionary<string, object?> values) => _values = values;
    public object? Get(string propertyName) => _values.TryGetValue(propertyName, out var value) ? value : null;
    public void Set(string propertyName, object? value) => _values[propertyName] = value;
    public bool HasProperty(string propertyName) => _values.ContainsKey(propertyName);
    public IReadOnlyCollection<string> PropertyNames => _values.Keys.ToArray();
    public object RawObject => _values;
}
