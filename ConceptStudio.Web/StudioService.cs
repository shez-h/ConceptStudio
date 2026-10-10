using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using OpenAI;
using OpenAI.Images;
using OpenAI.Responses;

#pragma warning disable OPENAI001

// Host-specific orchestration. The model requests and agent loop live in Core.
public sealed class StudioService(IWebHostEnvironment environment, IConfiguration configuration, SessionStore store)
{
    public ConcurrentDictionary<Guid, StudioSession> Sessions { get; } = new();
    public StudioSession CreateSession(string brief)
    {
        var session = new StudioSession { Brief = brief };
        Sessions[session.Id] = session;
        return session;
    }

    public async Task<StudioSession?> FindSessionAsync(Guid id, CancellationToken ct)
    {
        if (Sessions.TryGetValue(id, out var existing)) return existing;
        var saved = await store.LoadAsync(id, ct);
        return saved is null ? null : Sessions.GetOrAdd(id, saved);
    }

    public string TextModel => Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
    public string ImageModel => Environment.GetEnvironmentVariable("OPENAI_IMAGE_MODEL") ?? ConceptImageGenerator.DefaultModel;
    public string ReviewModel => Environment.GetEnvironmentVariable("OPENAI_REVIEW_MODEL") ?? ConceptImageReviewer.DefaultModel;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]);
    private ApiKeyCredential Credential() => new(configuration["OPENAI_API_KEY"]
        is { Length: > 0 } key ? key : throw new InvalidOperationException("Set OPENAI_API_KEY in the server environment, then restart the app."));
    private ResponsesClient TextClient() => new(Credential(), new ResponsesClientOptions
    {
        RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromSeconds(60)
    });
    private ImageClient Images() => new(ImageModel, Credential(), new OpenAIClientOptions
    {
        RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromMinutes(3)
    });
    private string OutputDirectory => Environment.GetEnvironmentVariable("CONCEPT_STUDIO_OUTPUT_DIR")
        ?? Path.Combine(environment.ContentRootPath, "artifacts", "images");

    public async Task<StudioSession> GenerateConceptsAsync(StudioSession session, string brief, int count, CancellationToken ct, Guid? referenceId = null, string referenceMode = "preserve")
    {
        var client = TextClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await using var mcp = await GuidelinesMcpClient.ConnectAsync(timeout.Token);
        var tools = await McpToolAdapter.DiscoverAsync(mcp, timeout.Token);
        List<string> trace = [];
        var request = ConceptGenerationRequest.Build(TextModel, brief, count, GetSketch(session, referenceId, referenceMode), tools.ToArray());
        var result = await new AgentRunner(client).RunAsync(request, trace.Add, timeout.Token);
        var concepts = ConceptGenerationRequest.ReadConcepts(result.Text, count, brief);
        session.Brief = brief;
        // Keep earlier concepts and revision indices stable when exploring another direction.
        session.Concepts.AddRange(concepts.Select(c => c with { ReferenceImageId = referenceId, ReferenceMode = referenceId is null ? null : referenceMode }));
        session.DraftReferenceId = referenceId; session.ReferenceMode = referenceMode;
        session.Trace.AddRange(trace);
        session.HasUnsavedChanges = true;
        return session;
    }

    public async Task<StudioRevision> GenerateImageAsync(StudioSession session, ImageRequest input, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var concept = session.Concepts[input.ConceptIndex];
        string path;
        if (input.SourceRevisionId is { } sourceId)
        {
            var source = session.Revisions[sourceId];
            path = await new ConceptImageEditor(Images()).EditAsync(concept, source.Path, input.Feedback!, OutputDirectory, timeout.Token);
        }
        else path = await new ConceptImageGenerator(Images()).GenerateAsync(concept, OutputDirectory, timeout.Token, GetSketch(session, concept.ReferenceImageId, concept.ReferenceMode ?? "preserve"));
        var revision = new StudioRevision(Guid.NewGuid(), input.ConceptIndex, path, input.SourceRevisionId,
            input.Feedback, DateTimeOffset.UtcNow);
        session.Revisions[revision.Id] = revision;
        session.HasUnsavedChanges = true;
        return revision;
    }

    private SketchInput? GetSketch(StudioSession session, Guid? id, string mode)
    {
        if (id is null) return null;
        var reference = session.References.Single(r => r.Id == id);
        return new SketchInput(store.ReferencePath(session.Id, reference), reference.MediaType, mode);
    }

    public async Task<ImageReview> ReviewAsync(StudioSession session, StudioRevision revision, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var review = await new ConceptImageReviewer(TextClient(), ReviewModel).ReviewAsync(
            revision.Path, session.Concepts[revision.ConceptIndex].AppliedGuidelines, timeout.Token);
        revision.Review = review;
        session.HasUnsavedChanges = true;
        return review;
    }
}
