namespace BibleEcho.Core.Models;

public record BibleVerse(
    int Id,
    string Version,
    string Book,
    int Chapter,
    int Verse,
    string Text
)
{
    public string FormattedReference => $"{Book} {Chapter}:{Verse}";
}
