using BibleEcho.Core.Interfaces;

namespace BibleEcho.AI;

public record CitationResults(List<object> Passages,List<string> Unresolved);

public static class CitationLookup {
    public static async Task<CitationResults> Resolve(string q,string? preceding,bool spoken,IBibleRepository repo,CancellationToken ct) {
        var passages = new List<object>();
        var unresolved = new List<string>();
        var citations = SpokenCitationLexer.Parse(q, speechAliases: spoken);
        if (spoken && !string.IsNullOrWhiteSpace(preceding)) {
            var earlier = SpokenCitationLexer.Parse(preceding, speechAliases: true);
            var combined = SpokenCitationLexer.Parse(preceding + " " + q, speechAliases: true);
            int consumed = 0;
            while (consumed < earlier.Count && consumed < combined.Count &&
                   earlier[consumed].FormattedReference == combined[consumed].FormattedReference) consumed++;
            citations = combined.Skip(consumed).ToList();
        }
        foreach (var reference in citations.DistinctBy(r => r.FormattedReference)) {
            var verses = await repo.GetVersesAsync(reference.Book,reference.Chapter,reference.VerseStart,reference.VerseEnd,cancellationToken:ct);
            if (verses.Count > 0 && (reference.VerseStart == null || verses[0].Verse == reference.VerseStart)
                && (reference.VerseEnd == null || verses[^1].Verse == reference.VerseEnd)) {
                passages.Add(new {reference=reference.FormattedReference, verses});
            } else {
                var chapter = await repo.GetVersesAsync(reference.Book,reference.Chapter,null,null,cancellationToken:ct);
                unresolved.Add(chapter.Count==0
                    ? $"Heard {reference.FormattedReference}. This chapter is unavailable in the KJV."
                    : $"Heard {reference.FormattedReference}. {reference.Book} {reference.Chapter} has verses 1–{chapter[^1].Verse}. Please repeat the reference.");
            }
        }
        return new(passages,unresolved);
    }
}
