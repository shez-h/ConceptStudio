using System.Text.Json;

// Persistence is separate from model calls. No API key is needed to create, save or open a design.
public sealed class SessionStore(string rootDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string Folder(Guid id) => Path.Combine(Path.GetFullPath(rootDirectory), id.ToString());
    private string ImagePath(Guid id, Guid revisionId) => Path.Combine(Folder(id), "images", revisionId.ToString(), "preview.png");

    public string ReferencePath(Guid sessionId, SessionReference reference)
    {
        string extension = reference.MediaType switch { "image/png" => ".png", "image/jpeg" => ".jpg",
            _ => throw new InvalidDataException("Unsupported reference type.") };
        return Path.Combine(Folder(sessionId), "references", reference.Id + extension);
    }

    public async Task<SessionReference> AddReferenceAsync(StudioSession session, string name, byte[] bytes, CancellationToken ct = default)
    {
        string mediaType = SessionReference.DetectMediaType(bytes);
        var reference = new SessionReference(Guid.NewGuid(), Path.GetFileName(name).Length is > 0 and <= 120
            ? Path.GetFileName(name) : "Reference image", mediaType);
        string path = ReferencePath(session.Id, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        session.References.Add(reference);
        session.DraftReferenceId = reference.Id;
        session.HasUnsavedChanges = true;
        return reference;
    }

    // Caller holds the session gate, so concepts/revisions cannot change during the snapshot.
    public async Task SaveAsync(StudioSession session, CancellationToken ct = default)
    {
        string folder = Folder(session.Id);
        Directory.CreateDirectory(folder);
        foreach (var revision in session.Revisions.Values)
        {
            string target = ImagePath(session.Id, revision.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                string temporaryImage = target + ".tmp";
                try
                {
                    await using (var source = File.OpenRead(revision.Path))
                    await using (var destination = File.Create(temporaryImage))
                        await source.CopyToAsync(destination, ct);
                    File.Move(temporaryImage, target, overwrite: true);
                }
                finally { if (File.Exists(temporaryImage)) File.Delete(temporaryImage); }
            }
        }
        DateTimeOffset savedAt = DateTimeOffset.UtcNow;
        var snapshot = new SessionSnapshot(1, session.Id, session.Name, session.Brief, savedAt,
            session.Concepts, session.Trace, session.SelectedRevisionId,
            session.Revisions.Values.OrderBy(r => r.CreatedAt).Select(r => new RevisionSnapshot(
                r.Id, r.ConceptIndex, r.SourceRevisionId, r.Feedback, r.CreatedAt, r.Review)).ToList(),
            session.References, session.DraftReferenceId, session.ReferenceMode);
        string temp = Path.Combine(folder, $"session-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, Json), ct);
            ct.ThrowIfCancellationRequested();
            // Replace only after all image copies and the complete JSON have been written.
            File.Move(temp, Path.Combine(folder, "session.json"), overwrite: true);
            session.SavedAt = savedAt;
            session.HasUnsavedChanges = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<StudioSession?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        string path = Path.Combine(Folder(id), "session.json");
        if (!File.Exists(path)) return null;
        var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllTextAsync(path, ct), Json)
            ?? throw new InvalidDataException("Saved session is empty.");
        if (snapshot.Version != 1 || snapshot.Id != id || snapshot.Concepts is null ||
            snapshot.Revisions is null || snapshot.Trace is null || string.IsNullOrWhiteSpace(snapshot.Name) ||
            snapshot.Brief is null || snapshot.Revisions.Select(r => r.Id).Distinct().Count() != snapshot.Revisions.Count ||
            snapshot.Concepts.Any(c => c is null || c.AppliedGuidelines is null))
            throw new InvalidDataException("Saved session format is invalid.");
        var session = new StudioSession
        {
            Id = id, Name = snapshot.Name, Brief = snapshot.Brief, SavedAt = snapshot.SavedAt,
            Concepts = snapshot.Concepts, Trace = snapshot.Trace, SelectedRevisionId = snapshot.SelectedRevisionId,
            References = snapshot.References ?? [], DraftReferenceId = snapshot.DraftReferenceId,
            ReferenceMode = snapshot.ReferenceMode ?? "preserve"
        };
        foreach (var revision in snapshot.Revisions)
        {
            if (revision.ConceptIndex < 0 || revision.ConceptIndex >= session.Concepts.Count)
                throw new InvalidDataException("Saved revision references an invalid concept.");
            session.Revisions[revision.Id] = new StudioRevision(revision.Id, revision.ConceptIndex,
                ImagePath(id, revision.Id), revision.SourceRevisionId, revision.Feedback, revision.CreatedAt)
                { Review = revision.Review };
        }
        return session;
    }

    public async Task<IReadOnlyList<SavedSessionSummary>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(rootDirectory)) return [];
        List<SavedSessionSummary> sessions = [];
        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileName(directory), out var id)) continue;
            try
            {
                var session = await LoadAsync(id, ct);
                if (session is not null) sessions.Add(new(id, session.Name, session.SavedAt!.Value, session.Concepts.Count));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                // Keep a damaged save visible so it isn't mistaken for lost work.
                sessions.Add(new(id, "Unreadable saved session", DateTimeOffset.MinValue, 0));
            }
        }
        return sessions.OrderByDescending(s => s.SavedAt).ToList();
    }
}

public sealed record SavedSessionSummary(Guid Id, string Name, DateTimeOffset SavedAt, int ConceptCount);
public sealed record SessionSnapshot(int Version, Guid Id, string Name, string Brief, DateTimeOffset SavedAt,
    List<Concept> Concepts, List<string> Trace, Guid? SelectedRevisionId, List<RevisionSnapshot> Revisions, List<SessionReference>? References = null,
    Guid? DraftReferenceId = null, string? ReferenceMode = null);
public sealed record RevisionSnapshot(Guid Id, int ConceptIndex, Guid? SourceRevisionId, string? Feedback,
    DateTimeOffset CreatedAt, ImageReview? Review);
