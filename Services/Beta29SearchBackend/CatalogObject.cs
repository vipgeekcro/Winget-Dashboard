using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using Beta29.SearchBackend.Compatibility;

namespace Beta29.SearchBackend;

/// <summary>The property bag produced by Windows PowerShell ConvertFrom-Json.</summary>
public sealed class CatalogObject
{
    private readonly Dictionary<string, object> properties;
    public IReadOnlyDictionary<string, object> Properties { get; }
    public object this[string name] => properties.TryGetValue(name, out object value) ? value : null;

    private CatalogObject(Dictionary<string, object> values)
    {
        properties = values;
        Properties = new ReadOnlyDictionary<string, object>(values);
    }

    internal static object Materialize(object value)
    {
        if (value is IDictionary<string, object> dictionary)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in dictionary)
            {
                if (string.IsNullOrEmpty(pair.Key))
                    throw new ArgumentException("ConvertFrom-Json cannot create an empty property name.");
                if (result.ContainsKey(pair.Key))
                    throw new ArgumentException("ConvertFrom-Json encountered keys differing only by case: " + pair.Key);
                result.Add(pair.Key, Materialize(pair.Value));
            }
            return new CatalogObject(result);
        }
        if (value is object[] array)
        {
            var result = new object[array.Length];
            for (int i = 0; i < array.Length; i++) result[i] = Materialize(array[i]);
            return result;
        }
        return value;
    }

    // PowerShell member access is case-insensitive and missing properties yield null.
    // Its member-access enumeration also handles an array used in place of an object.
    internal static object GetMember(object value, string name)
    {
        if (value is CatalogObject item) return item[name];
        if (value is object[] array)
        {
            var found = new List<object>();
            foreach (object child in array)
            {
                object property = GetMember(child, name);
                if (property is object[] nested) found.AddRange(nested);
                else if (property != null) found.Add(property);
            }
            return found.Count == 0 ? null : found.Count == 1 ? found[0] : found.ToArray();
        }
        return null;
    }

    internal static IEnumerable<object> Enumerate(object value)
    {
        if (value is object[] array) { foreach (object item in array) yield return item; }
        else if (value != null) yield return value;
    }

    public override string ToString()
    {
        if (properties.Count == 0) return string.Empty;
        var text = new StringBuilder("@{");
        bool first = true;
        foreach (var pair in properties)
        {
            if (!first) text.Append("; ");
            first = false;
            text.Append(pair.Key).Append('=');
            // PSObject.ToString(recurse:false) does not recursively expand a nested custom object.
            text.Append(pair.Value is CatalogObject ? string.Empty : FrameworkNumber.ConvertToString(pair.Value));
        }
        return text.Append('}').ToString();
    }
}
