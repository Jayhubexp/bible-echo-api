namespace BibleEcho.Core.Models;

public record ScriptureReference(
    string Book,
    int Chapter,
    int? VerseStart,
    int? VerseEnd = null,
    string? RawSpokenText = null
)
{
    public string FormattedReference
    {
        get
        {
            if (VerseStart is null)
                return $"{Book} {Chapter}";
            if (VerseEnd is null || VerseEnd == VerseStart)
                return $"{Book} {Chapter}:{VerseStart}";
            return $"{Book} {Chapter}:{VerseStart}-{VerseEnd}";
        }
    }
}
