namespace Lume.Core;

public sealed partial class Organizer
{
    private const int MaximumCatalogReferences = 25000;
    private CollectionCatalog? collectionCatalog;
    private readonly record struct SortViewKey(string Sort, bool Descending, IReadOnlyList<string>? Order);
    private sealed class CollectionCatalog(AppState state, IReadOnlyList<DesktopFile> files, long revision,
        Dictionary<string, Collection> collections, Dictionary<string, List<DesktopFile>> buckets)
    {
        internal AppState State { get; } = state;
        internal IReadOnlyList<DesktopFile> Files { get; } = files;
        internal long Revision { get; } = revision;
        internal Dictionary<string, Collection> Collections { get; } = collections;
        internal Dictionary<string, List<DesktopFile>> Buckets { get; } = buckets;
        internal Dictionary<(List<DesktopFile> Source, SortViewKey Sort), IReadOnlyList<DesktopFile>> Sorted { get; } = [];
        internal int SortedReferences { get; set; }
    }

    private CollectionCatalog Catalog()
    {
        if (collectionCatalog is { } cached && ReferenceEquals(cached.State, State) && ReferenceEquals(cached.Files, Files) && cached.Revision == contentRevision) return cached;
        var collections = State.Configuration.Collections.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var buckets = collections.Keys.ToDictionary(id => id, _ => new List<DesktopFile>(), StringComparer.Ordinal);
        var mapped = new Dictionary<string, List<DesktopFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in State.Configuration.Collections.Where(c => c.MappedPath != null))
        {
            if (!mapped.TryGetValue(collection.MappedPath!, out var source)) mapped[collection.MappedPath!] = source = [];
            buckets[collection.Id] = source;
        }
        var ordinary = State.Configuration.Collections.Where(c => c.MappedPath == null && !c.Recent).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var parent = mapped.Count == 0 ? null : System.IO.Path.GetDirectoryName(file.Path);
            var inMapping = parent != null && mapped.ContainsKey(parent);
            if (inMapping) mapped[parent!].Add(file);
            var assignment = CollectionOf(file);
            if (ordinary.Contains(assignment) && (!inMapping || State.Configuration.Overrides.ContainsKey(file.Path))) buckets[assignment].Add(file);
        }
        if (State.Configuration.Collections.Any(c => c.Recent))
        {
            var recent = Files.Where(f => !f.IsDirectory).OrderByDescending(f => f.ModifiedUtc).Take(40).ToList();
            foreach (var c in State.Configuration.Collections.Where(c => c.Recent)) buckets[c.Id] = recent;
        }
        var catalog = new CollectionCatalog(State, Files, contentRevision, collections, buckets);
        // Aliased mappings/recent views share one bucket. Each cached reference set remains bounded.
        if (Files.Count <= MaximumCatalogReferences && buckets.Values.Distinct().Sum(b => (long)b.Count) <= MaximumCatalogReferences) collectionCatalog = catalog;
        else collectionCatalog = null;
        return catalog;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<DesktopFile>> QueryCollections(IEnumerable<string> ids, string query = "")
    {
        var catalog = Catalog(); var terms = RuleEngine.SearchTerms(query);
        var active = catalog.Collections.Values.Select(c => (catalog.Buckets[c.Id], SortKey(c, Options(c.Id)))).ToHashSet();
        foreach (var key in catalog.Sorted.Keys.Where(k => !active.Contains(k)).ToArray())
        {
            catalog.SortedReferences -= catalog.Sorted[key].Count; catalog.Sorted.Remove(key);
        }
        var sorted = new Dictionary<(List<DesktopFile> Source, SortViewKey Sort), IReadOnlyList<DesktopFile>>(catalog.Sorted);
        var filtered = terms.Length == 0 ? null : new Dictionary<IReadOnlyList<DesktopFile>, IReadOnlyList<DesktopFile>>();
        var result = new Dictionary<string, IReadOnlyList<DesktopFile>>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!catalog.Collections.TryGetValue(id, out var collection)) throw new InvalidOperationException("分区已不存在，请刷新。");
            var options = Options(id);
            var source = catalog.Buckets[id]; var key = (source, SortKey(collection, options));
            if (!sorted.TryGetValue(key, out var view))
            {
                view = Array.AsReadOnly(collection.Recent ? source.ToArray() : SortCollection(source, options).ToArray()); sorted[key] = view;
                if (ReferenceEquals(collectionCatalog, catalog) && catalog.SortedReferences + view.Count <= MaximumCatalogReferences)
                { catalog.Sorted[key] = view; catalog.SortedReferences += view.Count; }
            }
            if (filtered == null) result[id] = view;
            else
            {
                if (!filtered.TryGetValue(view, out var matches)) filtered[view] = matches = Array.AsReadOnly(view.Where(f => RuleEngine.Search(f, terms)).ToArray());
                result[id] = matches;
            }
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<DesktopFile>>(result);
    }

    private static SortViewKey SortKey(Collection collection, CardOptions options) => collection.Recent
        ? new("recent", false, null)
        : options.Sort == "manual" ? new("manual", false, options.Order)
        : new(options.Sort is "type" or "size" or "modified" ? options.Sort : "name", options.Descending, null);

    private static IEnumerable<DesktopFile> SortCollection(IEnumerable<DesktopFile> source, CardOptions options)
    {
        if (options.Sort == "manual")
        {
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (options.Order != null) for (var i = 0; i < options.Order.Count; i++) order.TryAdd(options.Order[i], i);
            return source.OrderBy(f => order.GetValueOrDefault(f.Path, int.MaxValue)).ThenBy(f => f.Name);
        }
        IOrderedEnumerable<DesktopFile> SortBy<T>(Func<DesktopFile, T> key) => options.Descending ? source.OrderByDescending(key) : source.OrderBy(key);
        return options.Sort switch
        {
            "modified" => SortBy(f => f.ModifiedUtc).ThenBy(f => f.Name),
            "size" => SortBy(f => f.Size).ThenBy(f => f.Name),
            "type" => SortBy(f => f.Extension).ThenBy(f => f.Name),
            _ => SortBy(f => f.Name)
        };
    }
}
