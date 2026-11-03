using BibleEcho.Core.Models;

namespace BibleEcho.Core.Interfaces;

public interface IScriptureMatcher
{
    Task<ScriptureMatchResult?> MatchScriptureAsync(
        string transcribedText,
        string defaultVersion = "KJV",
        CancellationToken cancellationToken = default
    );
}
