using System.Collections.Concurrent;

public sealed class StudioSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled design";
    public string Brief { get; set; } = "";
    public List<Concept> Concepts { get; init; } = [];
    public List<string> Trace { get; init; } = [];
    public List<SessionReference> References { get; init; } = [];
    public Guid? DraftReferenceId { get; set; }
    public string ReferenceMode { get; set; } = "preserve";
    public bool HasUnsavedChanges { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public Guid? SelectedRevisionId { get; set; }
    public ConcurrentDictionary<Guid, StudioRevision> Revisions { get; } = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);
    // Caller holds Gate. Revision IDs stay stable when later concept indices shift.
    public bool DeleteConcept(int index)
    {
        if (index < 0 || index >= Concepts.Count) return false;
        Concepts.RemoveAt(index);
        foreach (var revision in Revisions.Values)
        {
            if (revision.ConceptIndex == index) Revisions.TryRemove(revision.Id, out _);
            else if (revision.ConceptIndex > index)
                Revisions[revision.Id] = new StudioRevision(revision.Id, revision.ConceptIndex - 1,
                    revision.Path, revision.SourceRevisionId, revision.Feedback, revision.CreatedAt)
                    { Review = revision.Review };
        }
        if (SelectedRevisionId is { } selected && !Revisions.ContainsKey(selected)) SelectedRevisionId = null;
        HasUnsavedChanges = true;
        return true;
    }

    public object View() => new { Id, Name, Brief, Concepts, Trace, SavedAt, SelectedRevisionId, HasUnsavedChanges, DraftReferenceId, ReferenceMode,
        references = References.Select(r => r.View(Id)),
        revisions = Revisions.Values.OrderBy(r => r.CreatedAt).Select(r => r.View(Id)) };
}

public sealed class StudioRevision(Guid id, int conceptIndex, string path, Guid? sourceRevisionId, string? feedback, DateTimeOffset createdAt)
{
    public Guid Id { get; } = id;
    public int ConceptIndex { get; } = conceptIndex;
    public string Path { get; } = path;
    public Guid? SourceRevisionId { get; } = sourceRevisionId;
    public string? Feedback { get; } = feedback;
    public ImageReview? Review { get; set; }
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public object View(Guid sessionId) => new { Id, ConceptIndex, SourceRevisionId, Feedback, CreatedAt, Review,
        imageUrl = $"/api/sessions/{sessionId}/images/{Id}" };
}
public sealed record BriefRequest(string Brief);
public sealed record GenerateConceptsRequest(string Brief, int Count = 3, Guid? ReferenceImageId = null, string ReferenceMode = "preserve");
public sealed record SaveSessionRequest(string Name, string Brief, Guid? SelectedRevisionId, Guid? DraftReferenceId = null, string ReferenceMode = "preserve");
public sealed record ImageRequest(int ConceptIndex, Guid? SourceRevisionId, string? Feedback);
