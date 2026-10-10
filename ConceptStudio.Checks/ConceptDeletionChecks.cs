public static class ConceptDeletionChecks
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"concept-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(root);
            var session = new StudioSession();
            foreach (string title in new[] { "First", "Middle", "Last" })
                session.Concepts.Add(new Concept(title, "Description", "Prompt", ["Guideline"], title + " brief"));
            string image = Path.Combine(root, "source.png");
            await File.WriteAllBytesAsync(image, [1, 2, 3]);
            var first = new StudioRevision(Guid.NewGuid(), 0, image, null, null, DateTimeOffset.UtcNow);
            var middle = new StudioRevision(Guid.NewGuid(), 1, image, null, null, DateTimeOffset.UtcNow);
            var last = new StudioRevision(Guid.NewGuid(), 2, image, null, null, DateTimeOffset.UtcNow);
            var edit = new StudioRevision(Guid.NewGuid(), 2, image, last.Id, "Edit", DateTimeOffset.UtcNow)
                { Review = new ImageReview([new GuidelineFinding(1, "pass", "Visible")]) };
            foreach (var revision in new[] { first, middle, last, edit }) session.Revisions[revision.Id] = revision;
            session.SelectedRevisionId = edit.Id;
            await store.SaveAsync(session);
            if (!session.DeleteConcept(1) || session.Concepts.Count != 2 || session.Concepts[1].Title != "Last" ||
                session.Revisions.ContainsKey(middle.Id) || session.Revisions[last.Id].ConceptIndex != 1 ||
                session.Revisions[edit.Id].ConceptIndex != 1 || session.Revisions[edit.Id].SourceRevisionId != last.Id ||
                session.Revisions[edit.Id].Review?.Findings[0].Verdict != "pass" ||
                session.SelectedRevisionId != edit.Id || !session.HasUnsavedChanges)
                throw new Exception("Deleting a middle concept corrupted remaining revisions.");
            if ((await store.LoadAsync(session.Id))!.Concepts.Count != 3)
                throw new Exception("Deletion must follow explicit save semantics.");
            await store.SaveAsync(session);
            session = (await store.LoadAsync(session.Id))!;
            if (session.Concepts.Count != 2 || session.Revisions.Count != 3 || session.Concepts[1].Brief != "Last brief")
                throw new Exception("Deletion did not survive saving/reopening.");
            if (!session.DeleteConcept(1) || session.SelectedRevisionId is not null || session.Revisions.Count != 1)
                throw new Exception("Selected deleted image was not cleared.");
            if (session.DeleteConcept(-1) || session.DeleteConcept(99) || session.Concepts.Count != 1)
                throw new Exception("Invalid deletion changed the session.");
            if (!session.DeleteConcept(0) || session.Concepts.Count != 0 || session.Revisions.Count != 0)
                throw new Exception("Deleting final concept must leave an empty session.");
            await store.SaveAsync(session);
            if ((await store.LoadAsync(session.Id))!.Concepts.Count != 0) throw new Exception("Empty session did not persist.");
            Console.WriteLine("PASS: Concept deletion preserves remaining revision associations, clears selection, validates indices and persists on save");
        }
        finally { Directory.Delete(root, true); }
    }
}
