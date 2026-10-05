namespace Lume.Core;

public enum ReferencePresence { Unavailable, Present, Missing }
public sealed record MissingReference(DateTime FirstMissingUtc, DateTime LastCheckedUtc, string ScopeIdentity);
public sealed record ReferenceObservation(string Path, ReferencePresence Presence, string? ScopeIdentity = null);
public sealed record StaleReference(string Path, DateTime FirstMissingUtc, bool Assignment, int Samples)
{ public string Name => System.IO.Path.GetFileName(Path); }

public sealed class ReferenceReviewScope
{
    public IReadOnlyList<string> Paths { get; }
    public IReadOnlyList<string> Roots { get; }
    public int ProtectedCount { get; }
    internal Organizer Owner { get; }
    internal AppState State { get; }
    internal IReadOnlyList<DesktopFile> Files { get; }
    internal long Revision { get; }
    internal ReferenceReviewScope(Organizer owner, AppState state, long revision, string[] paths, string[] roots, int protectedCount)
    { Owner = owner; State = state; Files = owner.Files; Revision = revision; Paths = Array.AsReadOnly(paths); Roots = Array.AsReadOnly(roots); ProtectedCount = protectedCount; }
}

public sealed class ReferenceReviewPlan
{
    public IReadOnlyList<StaleReference> Candidates { get; }
    public IReadOnlyList<string> Roots => Scope.Roots;
    public int GraceDays { get; }
    public int WaitingCount { get; }
    public int UnavailableCount { get; }
    public int ProtectedCount { get; }
    internal ReferenceReviewScope Scope { get; }
    internal IReadOnlyDictionary<string, MissingReference> Missing { get; }
    internal ReferenceReviewPlan(ReferenceReviewScope scope, StaleReference[] candidates, int days, int waiting, int unavailable,
        Dictionary<string, MissingReference> missing)
    { Scope = scope; Candidates = Array.AsReadOnly(candidates); GraceDays = days; WaitingCount = waiting; UnavailableCount = unavailable;
        ProtectedCount = scope.ProtectedCount; Missing = new System.Collections.ObjectModel.ReadOnlyDictionary<string, MissingReference>(missing); }
}

public sealed partial class Organizer
{
    private HashSet<string> ProtectedReferencePaths() => State.Configuration.Overrides.Keys.Concat(State.Configuration.LinkedFiles)
        .Concat(State.Desktop.Cards.Values.SelectMany(c => c.Order ?? []))
        .Concat(Files.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public ReferenceReviewScope CaptureReferenceReview()
    {
        var all = State.Assignments.Keys.Concat(State.Configuration.Samples.Select(s => s.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var protect = ProtectedReferencePaths(); var roots = WatchRoots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = all.Where(p => System.IO.Path.IsPathFullyQualified(p) && !protect.Contains(p)
            && roots.Contains(System.IO.Path.GetDirectoryName(p)!)).ToArray();
        return new(this, State, contentRevision, paths, roots.ToArray(), all.Length - paths.Length);
    }

    private void ValidateReview(ReferenceReviewScope scope)
    {
        if (!ReferenceEquals(scope.Owner, this) || !ReferenceEquals(scope.State, State) || !ReferenceEquals(scope.Files, Files) || scope.Revision != contentRevision)
            throw new InvalidOperationException("配置或文件列表已变化，请重新检测引用。");
    }

    private static Dictionary<string, ReferenceObservation> Observations(ReferenceReviewScope scope, IEnumerable<ReferenceObservation> values)
    {
        var result = new Dictionary<string, ReferenceObservation>(StringComparer.OrdinalIgnoreCase);
        var allowed = scope.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            if (!allowed.Contains(value.Path) || !Enum.IsDefined(value.Presence) || !result.TryAdd(value.Path, value))
                throw new InvalidDataException("引用检测返回了重复或不属于当前检测范围的路径。");
        return result;
    }

    // Only positive metadata evidence starts the grace period. An unavailable root clears it.
    public ReferenceReviewPlan ReviewReferences(ReferenceReviewScope scope, IEnumerable<ReferenceObservation> observations, int graceDays = 30, DateTime? checkedUtc = null)
    {
        ValidateReview(scope);
        if (graceDays is < 7 or > 365) throw new ArgumentOutOfRangeException(nameof(graceDays));
        var now = checkedUtc ?? DateTime.UtcNow;
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("检测时间必须为 UTC。", nameof(checkedUtc));
        var evidence = Observations(scope, observations); var protect = ProtectedReferencePaths();
        var next = new Dictionary<string, MissingReference>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<StaleReference>(); var waiting = 0; var unavailable = 0;
        var sampleCounts = State.Configuration.Samples.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in scope.Paths)
        {
            if (protect.Contains(path)) continue;
            if (!evidence.TryGetValue(path, out var observation) || observation.Presence == ReferencePresence.Unavailable
                || (observation.Presence == ReferencePresence.Missing && string.IsNullOrEmpty(observation.ScopeIdentity))) { unavailable++; continue; }
            if (observation.Presence == ReferencePresence.Present) continue;
            var first = State.MissingReferences.TryGetValue(path, out var previous) && previous.ScopeIdentity == observation.ScopeIdentity
                && previous.LastCheckedUtc <= now ? previous.FirstMissingUtc : now;
            next[path] = new(first, now, observation.ScopeIdentity!);
            if (now - first < TimeSpan.FromDays(graceDays)) { waiting++; continue; }
            candidates.Add(new(path, first, State.Assignments.ContainsKey(path), sampleCounts.GetValueOrDefault(path)));
        }
        if (next.Count != State.MissingReferences.Count || next.Any(p => !State.MissingReferences.TryGetValue(p.Key, out var value) || value != p.Value))
            Transaction(() => State.MissingReferences = next);
        var updated = new ReferenceReviewScope(this, State, contentRevision, scope.Paths.ToArray(), scope.Roots.ToArray(), scope.ProtectedCount);
        return new(updated, candidates.ToArray(), graceDays, waiting, unavailable, new(next, StringComparer.OrdinalIgnoreCase));
    }

    // Backup and metadata recheck precede the single state commit. User file contents are never touched.
    public async Task CleanReferencesAsync(ReferenceReviewPlan plan, string backupPath, string productVersion,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<ReferenceObservation>>> recheck, CancellationToken cancellation = default)
    {
        ValidateReview(plan.Scope);
        if (plan.Candidates.Count == 0) throw new InvalidOperationException("当前没有可清理的引用。");
        cancellation.ThrowIfCancellationRequested();
        store.Save(State);
        await Task.Run(() => DataBackup.Export(System.IO.Path.GetDirectoryName(store.Path)!, backupPath, productVersion), cancellation);
        cancellation.ThrowIfCancellationRequested(); ValidateReview(plan.Scope);
        var paths = plan.Candidates.Select(c => c.Path).ToArray();
        var checkedAgain = Observations(plan.Scope, await recheck(Array.AsReadOnly(paths), cancellation));
        cancellation.ThrowIfCancellationRequested(); ValidateReview(plan.Scope);
        var protect = ProtectedReferencePaths();
        foreach (var path in paths)
            if (protect.Contains(path) || !checkedAgain.TryGetValue(path, out var observation) || observation.Presence != ReferencePresence.Missing
                || observation.ScopeIdentity != plan.Missing[path].ScopeIdentity)
                throw new InvalidOperationException("部分文件已返回或目录无法确认，未清理任何引用；请重新检测。");
        Transaction(() =>
        {
            // Earlier builds cannot correctly undo removed assignments; reject loading instead of silently losing recovery data.
            State.Version = 3;
            var before = StateStore.Clone(State.Configuration);
            var removed = paths.Where(State.Assignments.ContainsKey).ToDictionary(p => p, p => State.Assignments[p], StringComparer.OrdinalIgnoreCase);
            var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths) { State.Assignments.Remove(path); State.MissingReferences.Remove(path); }
            State.Configuration.Samples.RemoveAll(s => selected.Contains(s.Path));
            State.History.Add(new() { Title = $"清理 {paths.Length} 项过期引用", Detail = $"原文件未修改；清理前完整备份：{backupPath}",
                PreviousConfiguration = before, RemovedAssignments = removed });
        });
    }
}
