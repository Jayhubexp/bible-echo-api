using System.Text.RegularExpressions;
using BibleEcho.Core.Models;

namespace BibleEcho.AI;

public static class SpokenCitationLexer
{
    private static readonly Dictionary<string, string> CanonicalBooks = new(StringComparer.OrdinalIgnoreCase)
    {
        // Old Testament
        ["genesis"] = "Genesis", ["gen"] = "Genesis",
        ["exodus"] = "Exodus", ["ex"] = "Exodus",
        ["leviticus"] = "Leviticus", ["lev"] = "Leviticus",
        ["numbers"] = "Numbers", ["num"] = "Numbers",
        ["deuteronomy"] = "Deuteronomy", ["deut"] = "Deuteronomy",
        ["joshua"] = "Joshua", ["josh"] = "Joshua",
        ["judges"] = "Judges", ["judg"] = "Judges",
        ["ruth"] = "Ruth",
        ["1 samuel"] = "I Samuel", ["1 sam"] = "I Samuel", ["first samuel"] = "I Samuel", ["i samuel"] = "I Samuel",
        ["2 samuel"] = "II Samuel", ["2 sam"] = "II Samuel", ["second samuel"] = "II Samuel", ["ii samuel"] = "II Samuel",
        ["1 kings"] = "I Kings", ["1 kgs"] = "I Kings", ["first kings"] = "I Kings", ["i kings"] = "I Kings",
        ["2 kings"] = "II Kings", ["2 kgs"] = "II Kings", ["second kings"] = "II Kings", ["ii kings"] = "II Kings",
        ["1 chronicles"] = "I Chronicles", ["1 chr"] = "I Chronicles", ["first chronicles"] = "I Chronicles", ["i chronicles"] = "I Chronicles",
        ["2 chronicles"] = "II Chronicles", ["2 chr"] = "II Chronicles", ["second chronicles"] = "II Chronicles", ["ii chronicles"] = "II Chronicles",
        ["ezra"] = "Ezra",
        ["nehemiah"] = "Nehemiah", ["neh"] = "Nehemiah",
        ["esther"] = "Esther",
        ["job"] = "Job",
        ["psalms"] = "Psalms", ["psalm"] = "Psalms", ["ps"] = "Psalms",
        ["proverbs"] = "Proverbs", ["prov"] = "Proverbs",
        ["ecclesiastes"] = "Ecclesiastes", ["eccl"] = "Ecclesiastes",
        ["song of solomon"] = "Song of Solomon", ["songs"] = "Song of Solomon", ["song of songs"] = "Song of Solomon",
        ["isaiah"] = "Isaiah", ["isa"] = "Isaiah",
        ["jeremiah"] = "Jeremiah", ["jer"] = "Jeremiah",
        ["lamentations"] = "Lamentations", ["lam"] = "Lamentations",
        ["ezekiel"] = "Ezekiel", ["ezek"] = "Ezekiel",
        ["daniel"] = "Daniel", ["dan"] = "Daniel",
        ["hosea"] = "Hosea", ["hos"] = "Hosea",
        ["joel"] = "Joel",
        ["amos"] = "Amos",
        ["obadiah"] = "Obadiah",
        ["jonah"] = "Jonah",
        ["micah"] = "Micah", ["mic"] = "Micah",
        ["nahum"] = "Nahum",
        ["habakkuk"] = "Habakkuk", ["hab"] = "Habakkuk",
        ["zephaniah"] = "Zephaniah", ["zeph"] = "Zephaniah",
        ["haggai"] = "Haggai", ["hag"] = "Haggai",
        ["zechariah"] = "Zechariah", ["zech"] = "Zechariah",
        ["malachi"] = "Malachi", ["mal"] = "Malachi",

        // New Testament
        ["matthew"] = "Matthew", ["matt"] = "Matthew",
        ["mark"] = "Mark", ["mk"] = "Mark",
        ["luke"] = "Luke", ["lk"] = "Luke",
        ["john"] = "John", ["jn"] = "John",
        ["acts"] = "Acts", ["acts of the apostles"] = "Acts",
        ["romans"] = "Romans", ["rom"] = "Romans",
        ["1 corinthians"] = "I Corinthians", ["1 cor"] = "I Corinthians", ["first corinthians"] = "I Corinthians", ["i corinthians"] = "I Corinthians",
        ["2 corinthians"] = "II Corinthians", ["2 cor"] = "II Corinthians", ["second corinthians"] = "II Corinthians", ["ii corinthians"] = "II Corinthians",
        ["galatians"] = "Galatians", ["gal"] = "Galatians",
        ["ephesians"] = "Ephesians", ["eph"] = "Ephesians",
        ["philippians"] = "Philippians", ["phil"] = "Philippians",
        ["colossians"] = "Colossians", ["col"] = "Colossians",
        ["1 thessalonians"] = "I Thessalonians", ["1 thess"] = "I Thessalonians", ["first thessalonians"] = "I Thessalonians", ["i thessalonians"] = "I Thessalonians",
        ["2 thessalonians"] = "II Thessalonians", ["2 thess"] = "II Thessalonians", ["second thessalonians"] = "II Thessalonians", ["ii thessalonians"] = "II Thessalonians",
        ["1 timothy"] = "I Timothy", ["1 tim"] = "I Timothy", ["first timothy"] = "I Timothy", ["i timothy"] = "I Timothy",
        ["2 timothy"] = "II Timothy", ["2 tim"] = "II Timothy", ["second timothy"] = "II Timothy", ["ii timothy"] = "II Timothy",
        ["titus"] = "Titus", ["tit"] = "Titus",
        ["philemon"] = "Philemon", ["phlm"] = "Philemon",
        ["hebrews"] = "Hebrews", ["heb"] = "Hebrews",
        ["james"] = "James", ["jas"] = "James",
        ["1 peter"] = "I Peter", ["1 pet"] = "I Peter", ["first peter"] = "I Peter", ["i peter"] = "I Peter",
        ["2 peter"] = "II Peter", ["2 pet"] = "II Peter", ["second peter"] = "II Peter", ["ii peter"] = "II Peter",
        ["1 john"] = "I John", ["1 jn"] = "I John", ["first john"] = "I John", ["i john"] = "I John",
        ["2 john"] = "II John", ["2 jn"] = "II John", ["second john"] = "II John", ["ii john"] = "II John",
        ["3 john"] = "III John", ["3 jn"] = "III John", ["third john"] = "III John", ["iii john"] = "III John",
        ["jude"] = "Jude",
        ["revelation"] = "Revelation of John", ["revelations"] = "Revelation of John", ["rev"] = "Revelation of John", ["revelation of john"] = "Revelation of John"
    };

    private static readonly Regex NumberWordsRegex = new(
        @"\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CitationPattern;
    private const string VerseMarker = @"\s*[.,;]?\s*(?:from\s+)?(?:verses?\b|v\.)\s*";
    // "look" is common sermon language. Require both an explicit chapter and verse.
    private static readonly Regex MisheardLuke = new(
        $@"\blook(?=\s+chapter\s+\d+{VerseMarker}\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static SpokenCitationLexer()
    {
        var sortedKeys = CanonicalBooks.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
        var booksPattern = string.Join("|", sortedKeys);

        // Pattern matches:
        // [Book Name] (chapter)? [ChapterNum] (:|verse)? [VerseNum] (to|-|through [VerseEndNum])?
        CitationPattern = new Regex(
            $@"\b({booksPattern})\s+(?:chapter\s+)?(\d+)(?:(?:\s*:\s*|{VerseMarker})(\d+)(?:\s*(?:-|to|through|–)\s*(?:verses?\s+)?(\d+))?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public static IReadOnlyList<ScriptureReference> Parse(string spokenText, bool speechAliases = false)
    {
        if (string.IsNullOrWhiteSpace(spokenText))
            return Array.Empty<ScriptureReference>();

        var normalized = NormalizeSpokenNumbers(spokenText);
        if (speechAliases) {
            normalized = Regex.Replace(normalized, @"\b(verses?)[.,]\s*(?=\d)", "$1 ", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\b([a-z]+)\1\b(?=\s+(?:chapter\s+)?\d)", m =>
                CanonicalBooks.ContainsKey(m.Groups[1].Value) ? m.Groups[1].Value : m.Value, RegexOptions.IgnoreCase);
            normalized = MisheardLuke.Replace(normalized, "Luke");
        }
        var matches = CitationPattern.Matches(normalized);
        var results = new List<ScriptureReference>();

        foreach (Match match in matches)
        {
            // A chunk can end mid-citation. Wait for the number before sending a slide.
            if (speechAliases && Regex.IsMatch(normalized[(match.Index+match.Length)..],
                @"^\s*[.,]?\s*(?:(?:from\s+)?verses?|(?:to|through)(?:\s+verses?)?|:)\s*[.,]?\s*$", RegexOptions.IgnoreCase))
                continue;
            var bookKey = match.Groups[1].Value.Trim();
            if (!CanonicalBooks.TryGetValue(bookKey, out var canonicalBook))
                continue;

            if (!int.TryParse(match.Groups[2].Value, out int chapter))
                continue;

            int? verseStart = null;
            if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out int vs))
            {
                verseStart = vs;
            }

            int? verseEnd = null;
            if (match.Groups[4].Success && int.TryParse(match.Groups[4].Value, out int ve))
            {
                verseEnd = ve;
            }

            results.Add(new ScriptureReference(
                canonicalBook,
                chapter,
                verseStart,
                verseEnd ?? verseStart,
                match.Value
            ));
        }

        return results;
    }

    private static string NormalizeSpokenNumbers(string text)
    {
        // Replace phrases like "first", "second", "third" at start of book names
        var t = text.Replace("1st ", "first ")
                    .Replace("2nd ", "second ")
                    .Replace("3rd ", "third ");

        // Convert spoken word numbers into digit strings
        t = NumberWordsRegex.Replace(t, m => ConvertWordToNumber(m.Value));
        t = Regex.Replace(t, @"\b([2-9])0[- ]([1-9])\b", "$1$2");
        return Regex.Replace(t, @"\b(?:1\s+)?hundred(?:\s+(?:and\s+)?(\d{1,2}))?\b",
            m => (100 + (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0)).ToString(),
            RegexOptions.IgnoreCase);
    }

    private static string ConvertWordToNumber(string word) =>
        word.ToLowerInvariant() switch
        {
            "one" => "1",
            "two" => "2",
            "three" => "3",
            "four" => "4",
            "five" => "5",
            "six" => "6",
            "seven" => "7",
            "eight" => "8",
            "nine" => "9",
            "ten" => "10",
            "eleven" => "11",
            "twelve" => "12",
            "thirteen" => "13",
            "fourteen" => "14",
            "fifteen" => "15",
            "sixteen" => "16",
            "seventeen" => "17",
            "eighteen" => "18",
            "nineteen" => "19",
            "twenty" => "20",
            "thirty" => "30",
            "forty" => "40",
            "fifty" => "50",
            "sixty" => "60",
            "seventy" => "70",
            "eighty" => "80",
            "ninety" => "90",
            _ => word
        };
}
