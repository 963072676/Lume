namespace Lume.Core;

public sealed record FileSearchResult(IReadOnlyList<DesktopFile> Files, bool HasMore);

/// <summary>Searches a stable scan snapshot without retaining an index or past queries.</summary>
public static class FileSearch
{
    public static FileSearchResult Find(IReadOnlyList<DesktopFile> files, string query, int limit = 40,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(query);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellation.ThrowIfCancellationRequested();
        var terms = RuleEngine.SearchTerms(query);
        var matches = new List<DesktopFile>(Math.Min(files.Count, limit));
        for (var i = 0; i < files.Count; i++)
        {
            if ((i & 63) == 0) cancellation.ThrowIfCancellationRequested();
            var file = files[i];
            if (!RuleEngine.Search(file, terms)) continue;
            if (matches.Count == limit)
            {
                cancellation.ThrowIfCancellationRequested();
                return new(matches.AsReadOnly(), true);
            }
            matches.Add(file);
        }
        cancellation.ThrowIfCancellationRequested();
        return new(matches.AsReadOnly(), false);
    }
}
