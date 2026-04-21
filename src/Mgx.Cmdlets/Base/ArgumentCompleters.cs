using System.Management.Automation;
using System.Management.Automation.Language;

namespace Mgx.Cmdlets.Base;

internal sealed class ConsistencyLevelCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        if ("eventual".StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            yield return new CompletionResult("eventual", "eventual", CompletionResultType.ParameterValue, "eventual consistency");
    }
}

internal sealed class ThrottlePriorityCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        string[] priorities = ["Low", "Normal", "High"];
        string[] tooltips = [
            "Deprioritize under throttling pressure",
            "Default priority",
            "Prioritize under throttling pressure"
        ];
        return priorities
            .Select((p, i) => (p, i))
            .Where(x => x.p.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            .Select(x => new CompletionResult(x.p, x.p, CompletionResultType.ParameterValue,
                tooltips[x.i]));
    }
}

internal sealed class ApiVersionCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        string[] versions = ["v1.0", "beta"];
        return versions
            .Where(v => v.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            .Select(v => new CompletionResult(v, v, CompletionResultType.ParameterValue, $"Graph API {v}"));
    }
}
