using System.Diagnostics;
using BibleEcho.Core.Interfaces;
using BibleEcho.Core.Models;

namespace BibleEcho.AI;

public class ScriptureMatchingEngine : IScriptureMatcher
{
    private readonly IBibleRepository _bibleRepository;

    public ScriptureMatchingEngine(IBibleRepository bibleRepository)
    {
        _bibleRepository = bibleRepository ?? throw new ArgumentNullException(nameof(bibleRepository));
    }

    public async Task<ScriptureMatchResult?> MatchScriptureAsync(
        string transcribedText,
        string defaultVersion = "KJV",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcribedText))
            return null;

        var stopwatch = Stopwatch.StartNew();

        // -------------------------------------------------------------
        // TIER 1: Spoken Citation Lexer & Parser (< 1ms execution)
        // Handles: "John 3:16", "First Corinthians 13:4-8", "Psalm 23"
        // -------------------------------------------------------------
        var parsedCitations = SpokenCitationLexer.Parse(transcribedText);
        if (parsedCitations.Count > 0)
        {
            var firstCitation = parsedCitations[0];
            var verses = await _bibleRepository.GetVersesAsync(
                firstCitation.Book,
                firstCitation.Chapter,
                firstCitation.VerseStart,
                firstCitation.VerseEnd,
                defaultVersion,
                cancellationToken
            );

            if (verses.Count > 0)
            {
                stopwatch.Stop();
                return new ScriptureMatchResult(
                    verses,
                    firstCitation.FormattedReference,
                    MatchTier.Tier1LexerCitation,
                    Confidence: 0.98f,
                    stopwatch.ElapsedMilliseconds,
                    transcribedText
                );
            }
        }

        // -------------------------------------------------------------
        // TIER 2: SQLite FTS5 Full-Text Match (< 5ms execution)
        // Handles verbatim quotes where the speaker did not mention the book name
        // (e.g. "For God so loved the world that he gave his only begotten Son")
        // -------------------------------------------------------------
        var ftsMatches = await _bibleRepository.SearchFullTextAsync(
            transcribedText,
            defaultVersion,
            limit: 5,
            cancellationToken
        );

        if (ftsMatches.Count > 0)
        {
            var topMatch = ftsMatches[0];
            stopwatch.Stop();
            return new ScriptureMatchResult(
                new[] { topMatch },
                topMatch.FormattedReference,
                MatchTier.Tier2FtsVerbatim,
                Confidence: 0.85f,
                stopwatch.ElapsedMilliseconds,
                transcribedText
            );
        }

        stopwatch.Stop();
        return null;
    }
}
