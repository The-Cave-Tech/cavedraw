using System.Reflection;
using VCCad.Core.Model;

namespace VCCad.Pdf.Tests.Corpus;

/// <summary>
/// Runtime inspection of what the document model can *represent*. The targeted
/// feature tests use it to pin today's gaps in a falsifiable way: a gap test
/// asserts that no model member expresses the concept, so the day the model gains
/// one the test fails loudly and must be rewritten as a positive assertion,
/// instead of silently passing forever.
/// </summary>
internal static class ModelCapabilities
{
    private static readonly Type[] ModelTypes = Load();

    /// <summary>
    /// True when any type name or public instance property name in
    /// <c>VCCad.Core.Model</c> satisfies <paramref name="predicate"/>.
    /// </summary>
    public static bool HasMemberMatching(Func<string, bool> predicate)
    {
        foreach (Type type in ModelTypes)
        {
            if (predicate(type.Name))
            {
                return true;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (predicate(property.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Type[] Load()
    {
        try
        {
            return typeof(PathItem).Assembly.GetTypes()
                .Where(type => type.Namespace == "VCCad.Core.Model")
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types
                .Where(type => type is not null && type.Namespace == "VCCad.Core.Model")
                .Select(type => type!)
                .ToArray();
        }
    }
}
