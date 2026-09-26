using System.Text.RegularExpressions;
using AuthCenter.Domain.Events;

namespace AuthCenter.UnitTests.Events;

/// <summary>Keeps the event type catalog in step with the audit events the code records.</summary>
public sealed partial class EventTypeCatalogTests
{
    [Fact]
    public void EveryAuditedAction_IsInTheCatalog()
    {
        var source = Path.Combine(RepositoryRoot(), "src");
        var audited = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match call in AuditCall().Matches(text))
            {
                foreach (Match literal in ActionLiteral().Matches(FirstArgument(text, call.Index + call.Length)))
                    audited.Add(literal.Groups[1].Value);
            }
            foreach (Match assignment in ActionAssignment().Matches(text))
                audited.Add(assignment.Groups[1].Value);
        }

        Assert.NotEmpty(audited);
        Assert.Equal([], audited.Where(action => !EventTypes.IsKnown(action)).ToArray());
    }

    [Fact]
    public void Catalog_HasEachTypeOnce()
    {
        Assert.Equal(EventTypes.All.Count, EventTypes.All.Select(item => item.Type).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(EventTypes.All, item => item.Type == EventTypes.Wildcard);
    }

    private static string FirstArgument(string text, int start)
    {
        var depth = 0;
        var end = start;
        for (; end < text.Length; end++)
        {
            var character = text[end];
            if (character is '(' or '[' or '{') depth++;
            else if (character is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (character == ',' && depth == 0) break;
        }
        return text[start..end];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuthCenter.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(@"\b(?:LogAsync|AddAudit|AuditAsync|AddRuleAudit|AddVersionAudit|AuditRejectedAsync)\s*\(")]
    private static partial Regex AuditCall();

    [GeneratedRegex(@"""([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)""")]
    private static partial Regex ActionLiteral();

    [GeneratedRegex(@"\bAction\s*=\s*""([A-Z][A-Z0-9_]+)""")]
    private static partial Regex ActionAssignment();
}
