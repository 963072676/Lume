namespace Lume.Core;

public sealed partial class Organizer
{
    private const int MaximumCatalogReferences = 25000;
    private CollectionCatalog? collectionCatalog;
    private sealed class CollectionCatalog(AppState state, IReadOnlyList<DesktopFile> files, long revision,
        Dictionary<string, Collection> collections, Dictionary<string, List<DesktopFile>> buckets)
    {
        internal AppState State { get; } = state;
        internal IReadOnlyList<DesktopFile> Files { get; } = files;
        internal long Revision { get; } = revision;
        internal Dictionary<string, Collection> Collections { get; } = collections;
        internal Dictionary<string, List<DesktopFile>> Buckets { get; } = buckets;
        internal Dictionary<string, (CardOptions Options, IReadOnlyList<DesktopFile> Files)> Sorted { get; } = [];
    }

    private CollectionCatalog Catalog()
    {
        if (collectionCatalog is { } cached && ReferenceEquals(cached.State, State) && ReferenceEquals(cached.Files, Files) && cached.Revision == contentRevision) return cached;
        var collections = State.Configuration.Collections.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var buckets = collections.Keys.ToDictionary(id => id, _ => new List<DesktopFile>(), StringComparer.Ordinal);
        var mapped = State.Configuration.Collections.Where(c => c.MappedPath != null)
            .GroupBy(c => c.MappedPath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        var ordinary = State.Configuration.Collections.Where(c => c.MappedPath == null && !c.Recent).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var parent = System.IO.Path.GetDirectoryName(file.Path);
            var inMapping = parent != null && mapped.TryGetValue(parent, out _);
            if (inMapping) foreach (var id in mapped[parent!]) buckets[id].Add(file);
            var assignment = CollectionOf(file);
            if (ordinary.Contains(assignment) && (!inMapping || State.Configuration.Overrides.ContainsKey(file.Path))) buckets[assignment].Add(file);
        }
        if (State.Configuration.Collections.Any(c => c.Recent))
        {
            var recent = Files.Where(f => !f.IsDirectory).OrderByDescending(f => f.ModifiedUtc).Take(40).ToList();
            foreach (var c in State.Configuration.Collections.Where(c => c.Recent)) buckets[c.Id] = recent;
        }
        var catalog = new CollectionCatalog(State, Files, contentRevision, collections, buckets);
        // Index and sorted views retain at most two bounded sets of references. Large overlapping mappings stay transient.
        if (Files.Count <= MaximumCatalogReferences && buckets.Values.Sum(b => (long)b.Count) <= MaximumCatalogReferences) collectionCatalog = catalog;
        else collectionCatalog = null;
        return catalog;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<DesktopFile>> QueryCollections(IEnumerable<string> ids, string query = "")
    {
        var catalog = Catalog(); var terms = RuleEngine.SearchTerms(query);
        var result = new Dictionary<string, IReadOnlyList<DesktopFile>>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!catalog.Collections.TryGetValue(id, out var collection)) throw new InvalidOperationException("分区已不存在，请刷新。");
            var options = Options(id);
            if (!catalog.Sorted.TryGetValue(id, out var cached) || cached.Options != options)
            {
                var source = catalog.Buckets[id];
                cached = (options, Array.AsReadOnly(collection.Recent ? source.ToArray() : SortCollection(source, options).ToArray()));
                catalog.Sorted[id] = cached;
            }
            result[id] = terms.Length == 0 ? cached.Files : Array.AsReadOnly(cached.Files.Where(f => RuleEngine.Search(f, terms)).ToArray());
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<DesktopFile>>(result);
    }

    private static IEnumerable<DesktopFile> SortCollection(IEnumerable<DesktopFile> source, CardOptions options)
    {
        if (options.Sort == "manual")
        {
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (options.Order != null) for (var i = 0; i < options.Order.Count; i++) order.TryAdd(options.Order[i], i);
            return source.OrderBy(f => order.GetValueOrDefault(f.Path, int.MaxValue)).ThenBy(f => f.Name);
        }
        IOrderedEnumerable<DesktopFile> SortBy<T>(Func<DesktopFile, T> key) => options.Descending ? source.OrderByDescending(key) : source.OrderBy(key);
        return (options.Sort switch
        {
            "modified" => SortBy(f => f.ModifiedUtc), "size" => SortBy(f => f.Size),
            "type" => SortBy(f => f.Extension), _ => SortBy(f => f.Name)
        }).ThenBy(f => f.Name);
    }
}
