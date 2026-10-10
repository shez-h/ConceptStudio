using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Responses;

#pragma warning disable OPENAI001

public static class ReviewChecks
{
    public static async Task RunAsync()
    {
        using var handler = new FakeReviewApi();
        using var http = new HttpClient(handler);
        ResponsesClient client = new(new ApiKeyCredential("test-only"), new ResponsesClientOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        });
        var reviewer = new ConceptImageReviewer(client, ConceptImageReviewer.DefaultModel);
        string directory = Path.Combine(Path.GetTempPath(), $"concept-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preview.png");
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
        try
        {
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(png));
            if ((await reviewer.ReviewAsync(path, [])).Findings.Count != 0 || handler.Calls != 0)
                throw new Exception("Empty guidelines should skip review.");
            var review = await reviewer.ReviewAsync(path, ["Exactly two windows."]);
            using var request = JsonDocument.Parse(handler.Body!);
            var root = request.RootElement;
            var content = root.GetProperty("input")[0].GetProperty("content");
            if (handler.Path != "/v1/responses" ||
                content[1].GetProperty("image_url").GetString() != "data:image/png;base64," + png ||
                !content[0].GetProperty("text").GetString()!.Contains("Exactly two windows.") ||
                !root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean() ||
                review.Findings[0].Verdict != "uncertain" || !File.Exists(Path.Combine(directory, "review.json")))
                throw new Exception("Review request or saved assessment incorrect.");
            foreach (string invalid in new[]
            {
                "{\"findings\":[]}",
                "{\"findings\":[{\"id\":2,\"verdict\":\"pass\",\"explanation\":\"Visible\"}]}",
                "{\"findings\":[{\"id\":1,\"verdict\":\"maybe\",\"explanation\":\"Visible\"}]}"
            })
            {
                try { ConceptImageReviewer.ReadReview(invalid, 1); throw new Exception("Invalid review accepted."); }
                catch (AgentResponseException) { }
            }
            foreach (string mediaType in new[] { "image/png", "image/jpeg" })
            {
                var sketch = new SketchInput(path, mediaType, "preserve");
                var conceptRequest = ConceptGenerationRequest.Build("test-model", "A rescue vehicle", 1, sketch);
                await new AgentRunner(client).RunAsync(conceptRequest);
                using var sent = JsonDocument.Parse(handler.Body!);
                var parts = sent.RootElement.GetProperty("input")[0].GetProperty("content");
                if (parts[1].GetProperty("image_url").GetString() != $"data:{mediaType};base64,{png}" ||
                    !parts[0].GetProperty("text").GetString()!.Contains(sketch.Guidance) ||
                    !parts[0].GetProperty("text").GetString()!.Contains("A rescue vehicle"))
                    throw new Exception("Concept generation must send the image bytes, MIME type, brief and reference mode.");
            }
            Console.WriteLine("PASS: Multimodal concept request sends brief, reference guidance and image data URL (simulated API)");
            handler.Status = HttpStatusCode.TooManyRequests;
            int before = handler.Calls;
            try { await reviewer.ReviewAsync(path, ["Exactly two windows."]); throw new Exception("Expected API error."); }
            catch (ClientResultException ex) when (ex.Status == 429) { }
            if (handler.Calls != before + 1 || !File.Exists(path)) throw new Exception("Review failure must preserve image without retrying.");
            Console.WriteLine("PASS: Vision PNG upload, strict review schema, saved findings, empty-guideline skip, invalid findings and API failure (simulated API)");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeReviewApi : HttpMessageHandler
    {
        public int Calls;
        public string? Body;
        public string? Path;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string json = Status == HttpStatusCode.OK ? JsonSerializer.Serialize(new
            {
                id = "resp_test", @object = "response", created_at = 1700000000, status = "completed",
                model = ConceptImageReviewer.DefaultModel,
                output = new[] { new { id = "msg_test", type = "message", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text = "{\"findings\":[{\"id\":1,\"verdict\":\"uncertain\",\"explanation\":\"The far side is hidden.\"}]}", annotations = Array.Empty<object>() } } } }
            }) : "{\"error\":{\"message\":\"Test rate limit\",\"type\":\"rate_limit_error\"}}";
            return new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
