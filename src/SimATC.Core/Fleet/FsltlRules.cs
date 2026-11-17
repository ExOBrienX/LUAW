using System.Xml;

namespace SimATC.Core.Fleet;

/// <summary>
/// Regla de emparejamiento de FSLTL (FSLTL_Rules.vmr, V-08): para un tipo OACI y, opcionalmente, una aerolínea,
/// los modelos a usar. <see cref="Airline"/> vacío = regla genérica del tipo.
/// </summary>
public sealed record FsltlRule(string Airline, string TypeCode, IReadOnlyList<string> Models);

public static class FsltlRules
{
    public const string FileName = "FSLTL_Rules.vmr";

    /// <summary>
    /// Lee el XML de reglas: <c>&lt;ModelMatchRule CallsignPrefix="JAT" TypeCode="A320" ModelName="A//B//C" /&gt;</c>.
    /// Los modelos alternativos vienen separados por "//".
    /// </summary>
    public static List<FsltlRule> Parse(TextReader text)
    {
        var rules = new List<FsltlRule>();
        using var reader = XmlReader.Create(text, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "ModelMatchRule")
                continue;
            var type = reader.GetAttribute("TypeCode")?.Trim().ToUpperInvariant();
            var names = reader.GetAttribute("ModelName");
            if (string.IsNullOrEmpty(type) || string.IsNullOrWhiteSpace(names))
                continue;
            var airline = reader.GetAttribute("CallsignPrefix")?.Trim().ToUpperInvariant() ?? "";
            var models = names.Split("//", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            rules.Add(new FsltlRule(airline, type, models));
        }
        return rules;
    }
}
