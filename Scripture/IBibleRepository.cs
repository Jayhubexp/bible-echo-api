using BibleEcho.Core.Models;

namespace BibleEcho.Core.Interfaces;

public interface IBibleRepository
{
    Task<IReadOnlyList<BibleVerse>> GetVersesAsync(
        string book,
        int chapter,
        int? verseStart = null,
        int? verseEnd = null,
        string version = "KJV",
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<BibleVerse>> SearchFullTextAsync(
        string query,
        string version = "KJV",
        int limit = 10,
        CancellationToken cancellationToken = default
    );

}
