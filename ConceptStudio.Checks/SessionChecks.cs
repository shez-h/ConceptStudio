using System.Text.Json;

public static class SessionChecks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"studio-session-check-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SessionStore(Path.Combine(directory, "sessions"));
            foreach (
                var invalid in new[]
                {
                    Array.Empty<byte>(),
                    "<svg></svg>"u8.ToArray(),
                    new byte[SessionReference.MaxBytes + 1],
                }
            )
            {
                try
                {
                    SessionReference.DetectMediaType(invalid);
                    throw new Exception("Invalid upload accepted");
                }
                catch (ArgumentException) { }
            }
            var draft = new StudioSession { Name = "First idea", Brief = "Just a brief" };
            await store.SaveAsync(draft);
            var restored = await new SessionStore(Path.Combine(directory, "sessions")).LoadAsync(
                draft.Id
            );
            if (restored?.Brief != draft.Brief || restored.Concepts.Count != 0)
                throw new Exception("Draft must survive a fresh store instance before generation.");

            draft.Concepts.Add(
                new Concept(
                    "Explorer",
                    "A friendly vehicle",
                    "A rounded vehicle",
                    ["Exactly two windows."]
                )
            );
            string source = Path.Combine(directory, "original.png");
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="
            );
            await File.WriteAllBytesAsync(source, png);
            var reference = await store.AddReferenceAsync(draft, "../../sketch.png", png);
            draft.ReferenceMode = "inspiration";
            draft.Concepts[0] = draft.Concepts[0] with
            {
                ReferenceImageId = reference.Id,
                ReferenceMode = "preserve",
            };
            var original = new StudioRevision(
                Guid.NewGuid(),
                0,
                source,
                null,
                null,
                DateTimeOffset.UtcNow
            );
            var edit = new StudioRevision(
                Guid.NewGuid(),
                0,
                source,
                original.Id,
                "Remove extra window",
                DateTimeOffset.UtcNow
            )
            {
                Review = new ImageReview([
                    new GuidelineFinding(1, "uncertain", "Far side is hidden."),
                ]),
            };
            draft.Revisions[original.Id] = original;
            draft.Revisions[edit.Id] = edit;
            draft.SelectedRevisionId = edit.Id;
            draft.Trace.Add("Guidelines retrieved");
            await store.SaveAsync(draft);
            File.Delete(source); // Saved sessions must own their image copies.
            restored = await new SessionStore(Path.Combine(directory, "sessions")).LoadAsync(
                draft.Id
            );
            if (
                restored is null
                || restored.Id != draft.Id
                || restored.Revisions.Count != 2
                || restored.SelectedRevisionId != edit.Id
                || restored.Concepts[0].AppliedGuidelines[0] != "Exactly two windows."
                || restored.Revisions[edit.Id].Feedback != "Remove extra window"
                || restored.Revisions[edit.Id].SourceRevisionId != original.Id
                || restored.Revisions[edit.Id].Review?.Findings[0].Verdict != "uncertain"
                || !File.ReadAllBytes(restored.Revisions[edit.Id].Path).SequenceEqual(png)
            )
                throw new Exception(
                    "Session did not restore complete concepts, guidelines, revision history and review."
                );
            if (restored.Concepts[0].Brief is not null)
                throw new Exception("Older concepts must not be assigned an invented brief.");
            restored.Concepts.Add(
                new Concept("Forest", "A forest vehicle", "Forest prompt", [], "A new forest idea")
            );
            restored.Brief = "Another draft, not the original brief";
            await store.SaveAsync(restored);
            restored = (await store.LoadAsync(draft.Id))!;
            if (
                restored.Concepts.Count != 2
                || restored.Concepts[1].Brief != "A new forest idea"
                || restored.Concepts[0].Brief is not null
                || restored.Revisions[original.Id].ConceptIndex != 0
            )
                throw new Exception(
                    "Appending concepts must preserve individual briefs and existing revision associations."
                );
            if (
                restored.References.Count != 1
                || restored.DraftReferenceId != reference.Id
                || restored.ReferenceMode != "inspiration"
                || restored.Concepts[0].ReferenceImageId != reference.Id
                || restored.Concepts[0].ReferenceMode != "preserve"
                || restored.References[0].Name != "sketch.png"
                || !File.ReadAllBytes(store.ReferencePath(restored.Id, restored.References[0]))
                    .SequenceEqual(png)
            )
                throw new Exception(
                    "References must survive save/reopen with their original concept-specific modes and pixels."
                );
            restored.Name = "Renamed idea";
            await store.SaveAsync(restored);
            if ((await store.ListAsync()).Single().Name != "Renamed idea")
                throw new Exception("Save must update existing session.");
            restored.Revisions[Guid.NewGuid()] = new StudioRevision(
                Guid.NewGuid(),
                0,
                source,
                null,
                null,
                DateTimeOffset.UtcNow
            );
            restored.Name = "Should not overwrite";
            try
            {
                await store.SaveAsync(restored);
                throw new Exception("Expected missing source to fail.");
            }
            catch (FileNotFoundException) { }
            if ((await store.LoadAsync(draft.Id))!.Name != "Renamed idea")
                throw new Exception("Failed save replaced the last valid snapshot.");
            if (await store.LoadAsync(Guid.NewGuid()) is not null)
                throw new Exception("Unknown session must return null.");
            var other = new StudioSession { Name = "Keep me" };
            await store.SaveAsync(other);
            string referencePath = store.ReferencePath(restored.Id, restored.References[0]);
            store.Delete(restored);
            if (
                !restored.IsDeleted
                || await store.LoadAsync(restored.Id) is not null
                || File.Exists(referencePath)
                || (await store.ListAsync()).Single().Id != other.Id
            )
                throw new Exception(
                    "Deleting a session must remove its owned files and preserve other sessions."
                );
            try
            {
                await store.SaveAsync(restored);
                throw new Exception("Deleted session was resurrected.");
            }
            catch (InvalidOperationException) { }
            store.Delete(new StudioSession()); // An unsaved session has no directory.
            Console.WriteLine(
                "PASS: Session deletion removes saved files, preserves other sessions and prevents stale saves"
            );
            Console.WriteLine(
                "PASS: Draft save, fresh-store reload, guidelines, image copies, revision history/reviews, rename, listing and failed-save preservation"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
