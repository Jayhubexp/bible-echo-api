namespace BibleEcho.Core.Models;

public enum MatchTier
{
    Tier1LexerCitation,
    Tier2FtsVerbatim,
    Tier3SemanticVector,
    Tier4LlmDisambiguation
}

public record ScriptureMatchResult(
    IReadOnlyList<BibleVerse> Verses,
    string MatchedReference,
    MatchTier Tier,
    float Confidence,
    long LatencyMs,
    string OriginalQuery
);
