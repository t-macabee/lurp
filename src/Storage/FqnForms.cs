using System.Text;

namespace Lurp.Storage;

/// <summary>
///     Derived forms of stored fully qualified symbol names (D117, D181.2).
/// </summary>
public static class FqnForms
{
    /// <summary>
    ///     Removes every balanced <c>&lt;...&gt;</c> group (nested groups included) from
    ///     <paramref name="fqn" />, keeping any <c>global::</c> prefix. Returns
    ///     <paramref name="fqn" /> unchanged when angle brackets do not balance.
    /// </summary>
    public static string RemoveTypeParameterLists(string fqn)
    {
        ArgumentNullException.ThrowIfNull(fqn);

        if (fqn.IndexOf('<') < 0 && fqn.IndexOf('>') < 0)
            return fqn;

        var builder = new StringBuilder(fqn.Length);
        var depth = 0;

        foreach (var ch in fqn)
        {
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                if (depth == 0)
                    return fqn;
                depth--;
            }
            else if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return depth == 0 ? builder.ToString() : fqn;
    }
}
