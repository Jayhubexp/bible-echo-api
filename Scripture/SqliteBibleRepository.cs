using System.Data;
using BibleEcho.Core.Interfaces;
using BibleEcho.Core.Models;
using Microsoft.Data.Sqlite;

namespace BibleEcho.Data;

public class SqliteBibleRepository : IBibleRepository
{
    private readonly string _connectionString;

    public SqliteBibleRepository(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<IReadOnlyList<BibleVerse>> GetVersesAsync(
        string book,
        int chapter,
        int? verseStart = null,
        int? verseEnd = null,
        string version = "KJV",
        CancellationToken cancellationToken = default)
    {
        var verses = new List<BibleVerse>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string sql;
        if (verseStart.HasValue && verseEnd.HasValue && verseStart != verseEnd)
        {
            sql = "SELECT id, version, book, chapter, verse, text FROM bible WHERE version=@version AND book=@book COLLATE NOCASE AND chapter=@chapter AND verse BETWEEN @start AND @end ORDER BY verse ASC";
        }
        else if (verseStart.HasValue)
        {
            sql = "SELECT id, version, book, chapter, verse, text FROM bible WHERE version=@version AND book=@book COLLATE NOCASE AND chapter=@chapter AND verse=@start ORDER BY verse ASC";
        }
        else
        {
            sql = "SELECT id, version, book, chapter, verse, text FROM bible WHERE version=@version AND book=@book COLLATE NOCASE AND chapter=@chapter ORDER BY verse ASC";
        }

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@book", book);
        command.Parameters.AddWithValue("@chapter", chapter);
        if (verseStart.HasValue)
            command.Parameters.AddWithValue("@start", verseStart.Value);
        if (verseEnd.HasValue)
            command.Parameters.AddWithValue("@end", verseEnd.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            verses.Add(new BibleVerse(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5)
            ));
        }

        return verses;
    }

    public async Task<IReadOnlyList<BibleVerse>> SearchFullTextAsync(
        string query,
        string version = "KJV",
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<BibleVerse>();

        var cleanQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(cleanQuery))
            return Array.Empty<BibleVerse>();

        var results = new List<BibleVerse>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string ftsSql = @"
            SELECT verse_id, version, book, chapter, verse, text, rank
            FROM bible_fts
            WHERE version = @version AND bible_fts MATCH @query
            ORDER BY rank
            LIMIT @limit;
        ";

        try
        {
            await using var command = new SqliteCommand(ftsSql, connection);
            command.Parameters.AddWithValue("@version", version);
            command.Parameters.AddWithValue("@query", cleanQuery);
            command.Parameters.AddWithValue("@limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new BibleVerse(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetString(5)
                ));
            }
        }
        catch (SqliteException)
        {
            // Fallback to LIKE query if FTS query syntax error occurred
            return await FallbackLikeSearchAsync(query, version, limit, connection, cancellationToken);
        }

        return results;
    }

    private static async Task<IReadOnlyList<BibleVerse>> FallbackLikeSearchAsync(
        string query,
        string version,
        int limit,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var fallbackList = new List<BibleVerse>();
        const string likeSql = @"
            SELECT id, version, book, chapter, verse, text
            FROM bible
            WHERE version = @version AND text LIKE @term
            LIMIT @limit;
        ";
        await using var command = new SqliteCommand(likeSql, connection);
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@term", $"%{query}%");
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            fallbackList.Add(new BibleVerse(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5)
            ));
        }

        return fallbackList;
    }

    private static string SanitizeFtsQuery(string input)
    {
        // Remove special FTS syntax characters: " * ^ : NOT AND OR NEAR
        var tokens = input
            .Replace("\"", " ")
            .Replace("'", " ")
            .Replace("*", " ")
            .Replace("^", " ")
            .Replace(":", " ")
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Select(w => $"\"{w}\"");

        return string.Join(" ", tokens);
    }
}

