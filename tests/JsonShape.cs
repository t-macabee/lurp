using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lurp.Tests;

/// <summary>
///     Reduces a JSON payload to a sorted list of "path: type" lines that describe
///     the shape of the output without its values. Used by
///     <see cref="OutputContractSnapshotTests"/> to assert that CLI and MCP output
///     contracts stay stable.
/// </summary>
public static class JsonShape
{
    private static readonly Regex FixedKey = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    ///     Extracts the shape of a JSON element into a dictionary of path → type-set.
    ///     Every path that appears in the payload is recorded with the union of all
    ///     value types seen at that path. A property key that does not match the
    ///     fixed-field pattern is appended to <paramref name="violations"/> (prefixed
    ///     with <paramref name="surface"/>) and its value is not descended into.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Extract(
        JsonElement element,
        HashSet<string> mapPaths,
        string surface,
        List<string> violations,
        string path = "$")
    {
        var shapes = new Dictionary<string, HashSet<string>>();
        ExtractInner(element, path, shapes, mapPaths, surface, violations);
        return shapes;
    }

    private static void ExtractInner(
        JsonElement element,
        string path,
        Dictionary<string, HashSet<string>> shapes,
        HashSet<string> mapPaths,
        string surface,
        List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Add(shapes, path, "object");

                if (mapPaths.Contains(path))
                {
                    // The object's full path is a known data map: every key is data,
                    // so record each value under "{*}" whatever the keys look like.
                    foreach (var prop in element.EnumerateObject())
                        ExtractInner(prop.Value, path + ".{*}", shapes, mapPaths, surface, violations);
                }
                else
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (!FixedKey.IsMatch(prop.Name))
                        {
                            violations.Add($"{surface} {path}: key '{prop.Name}'");
                            continue; // do not descend into a value with a schema-invalid key
                        }

                        ExtractInner(prop.Value, path + "." + prop.Name, shapes, mapPaths, surface, violations);
                    }
                }
                break;

            case JsonValueKind.Array:
                int count = element.GetArrayLength();
                if (count == 0)
                    Add(shapes, path, "array(empty)");
                else
                {
                    Add(shapes, path, "array");
                    foreach (var item in element.EnumerateArray())
                        ExtractInner(item, path + "[]", shapes, mapPaths, surface, violations);
                }
                break;

            case JsonValueKind.String:
                Add(shapes, path, "string");
                break;

            case JsonValueKind.Number:
                Add(shapes, path, "number");
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                Add(shapes, path, "boolean");
                break;

            case JsonValueKind.Null:
                Add(shapes, path, "null");
                break;
        }
    }

    private static void Add(Dictionary<string, HashSet<string>> shapes, string path, string type)
    {
        if (!shapes.TryGetValue(path, out var types))
        {
            types = new HashSet<string>();
            shapes[path] = types;
        }
        types.Add(type);
    }

    /// <summary>
    ///     Formats a type set into the canonical "a|b|c" string. The "array(empty)"
    ///     marker is dropped when "array" is also present (the array had elements in
    ///     some occurrence).
    /// </summary>
    public static string FormatTypes(HashSet<string> types)
    {
        bool hasArray = types.Contains("array");
        bool hasEmptyArray = types.Contains("array(empty)");

        var result = new List<string>();
        if (hasArray)
            result.Add("array");
        else if (hasEmptyArray)
            result.Add("array(empty)");

        foreach (var t in types
            .Where(t => t != "array" && t != "array(empty)")
            .OrderBy(t => t, StringComparer.Ordinal))
            result.Add(t);

        return string.Join("|", result);
    }

    /// <summary>
    ///     Formats shapes into sorted "surface path types" lines.
    /// </summary>
    public static List<string> Format(string surface, Dictionary<string, HashSet<string>> shapes)
    {
        return shapes
            .OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => $"{surface} {k.Key} {FormatTypes(k.Value)}")
            .ToList();
    }

}
