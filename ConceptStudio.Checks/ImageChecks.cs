using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Images;

public static class ImageChecks
{
    public static async Task RunAsync()
    {
        using var handler = new FakeImageApi();
        using var http = new HttpClient(handler);
        var client = new ImageClient(ConceptImageGenerator.DefaultModel, new ApiKeyCredential("test-only"),
            new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) });
        var generator = new ConceptImageGenerator(client);
        string directory = Path.Combine(Path.GetTempPath(), $"concept-image-check-{Guid.NewGuid():N}");
        try
        {
            var concept = new Concept("Explorer", "Yellow and teal, two windows", "Rounded underwater vehicle",
                ["Include exactly two circular observation windows."]);
            string imagePath = await generator.GenerateAsync(concept, directory);
            if (!File.ReadAllBytes(imagePath).SequenceEqual(Convert.FromBase64String(FakeImageApi.Png)))
                throw new Exception("Image bytes were not decoded/saved correctly.");

            using var request = JsonDocument.Parse(handler.Body!);
            var root = request.RootElement;
            if (handler.Path != "/v1/images/generations" ||
                root.GetProperty("model").GetString() != ConceptImageGenerator.DefaultModel ||
                root.GetProperty("quality").GetString() != "low" ||
                root.GetProperty("size").GetString() != "1024x1024" ||
                root.GetProperty("output_format").GetString() != "png" ||
                root.TryGetProperty("response_format", out _) ||
                !root.GetProperty("prompt").GetString()!.Contains(concept.ImagePrompt) ||
                !root.GetProperty("prompt").GetString()!.Contains(concept.AppliedGuidelines[0]))
                throw new Exception("Incorrect image API request.");

            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(imagePath)!, "request.json")));
            if (saved.RootElement.GetProperty("prompt").GetString() != root.GetProperty("prompt").GetString())
                throw new Exception("Saved prompt differs from the sent prompt.");
            Console.WriteLine("PASS: Image endpoint, model/settings, prompt capture, base64 decoding, PNG save (simulated API)");

            var editor = new ConceptImageEditor(client);
            const string feedback = "Remove the third window; preserve the rest of the design.";
            string editedPath = await editor.EditAsync(concept, imagePath, feedback, directory);
            if (handler.Path != "/v1/images/edits" ||
                handler.ContentType != "multipart/form-data" ||
                !handler.Body!.Contains(feedback) ||
                !handler.Body.Contains(concept.AppliedGuidelines[0]) ||
                !handler.Body.Contains(ConceptImageGenerator.DefaultModel) ||
                handler.Bytes!.AsSpan().IndexOf(Convert.FromBase64String(FakeImageApi.Png)) < 0)
                throw new Exception("Edit request must upload the source PNG with feedback and guidelines.");
            if (editedPath == imagePath || !File.Exists(editedPath) ||
                !File.ReadAllBytes(imagePath).SequenceEqual(Convert.FromBase64String(FakeImageApi.Png)))
                throw new Exception("Editing must save a new image and preserve the original.");
            using var editRecord = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(Path.GetDirectoryName(editedPath)!, "request.json")));
            if (editRecord.RootElement.GetProperty("sourceImagePath").GetString() != imagePath ||
                editRecord.RootElement.GetProperty("feedback").GetString() != feedback)
                throw new Exception("Edit record must identify its source and feedback.");
            Console.WriteLine("PASS: Edit endpoint uploads PNG bytes, feedback and guidelines; saves a separate revision with provenance");

            int callsBeforeMissingFile = handler.Calls;
            try
            {
                await editor.EditAsync(concept, Path.Combine(directory, "missing.png"), feedback, directory);
                throw new Exception("Expected missing input failure.");
            }
            catch (FileNotFoundException) { }
            if (handler.Calls != callsBeforeMissingFile) throw new Exception("Missing source must fail before an API call.");

            handler.Status = HttpStatusCode.TooManyRequests;
            int before = handler.Calls;
            try
            {
                await generator.GenerateAsync(concept, directory);
                throw new Exception("Expected image API error.");
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                if (handler.Calls != before + 1) throw new Exception("Unexpected automatic retry.");
            }
            Console.WriteLine("PASS: Image API errors propagate without automatic retries");
            before = handler.Calls;
            try
            {
                await editor.EditAsync(concept, editedPath, feedback, directory);
                throw new Exception("Expected edit API error.");
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                if (handler.Calls != before + 1 || !File.Exists(editedPath))
                    throw new Exception("Failed edit must not retry or delete the previous revision.");
            }
            Console.WriteLine("PASS: Missing edit input fails locally; edit API errors preserve the previous image without retrying");

            TextReader originalInput = Console.In;
            try
            {
                Console.SetIn(new StringReader("invalid\n\n"));
                if (await ImagePreviewFlow.RunAsync([concept], "unused-test-key") != 0)
                    throw new Exception("Skipping image generation should succeed.");
            }
            finally { Console.SetIn(originalInput); }
            Console.WriteLine("PASS: Invalid selection can be corrected by skipping, without an API call");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeImageApi : HttpMessageHandler
    {
        public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
        public string? Body { get; private set; }
        public byte[]? Bytes { get; private set; }
        public string? ContentType { get; private set; }
        public string? Path { get; private set; }
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            ContentType = request.Content!.Headers.ContentType?.MediaType;
            Bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Body = Encoding.UTF8.GetString(Bytes);
            string json = Status == HttpStatusCode.OK
                ? JsonSerializer.Serialize(new { created = 1700000000, data = new[] { new { b64_json = Png } } })
                : """{"error":{"message":"Test rate limit","type":"rate_limit_error","code":"rate_limit_exceeded"}}""";
            return new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
