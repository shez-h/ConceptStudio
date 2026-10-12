# Concept Studio — LLM integration lab

A C# learning project with console and web interfaces for structured output, function calling, MCP, image generation, editing, and vision review.

[Interview one-pager](INTERVIEW-NOTES.md): living notes on the agent loop, MCP, context, structured output and image feedback.

## Run

Build all five projects from the repository root:

```sh
dotnet build ConceptStudio.sln
```

Open `ConceptStudio.sln` in your IDE to work with the console, web, shared core, MCP server, and checks together.

Configure `OPENAI_API_KEY` in your environment, then:

```sh
dotnet run --project ConceptStudio.Console
```

Optional: set `OPENAI_MODEL` to override `gpt-4.1-mini`. API calls are paid and model access depends on your project. The app does not load `.env` files itself.

To enter a key privately for one macOS zsh session:

```zsh
read -rs 'OPENAI_API_KEY?Paste your API key (hidden): '; echo
export OPENAI_API_KEY
```

Building the console app also builds `ConceptStudio.GuidelinesMcp` and copies its runnable files into the console output's `mcp-server` directory. The app starts and disposes the MCP server automatically. This setup targets local `dotnet run`/VS Code builds; publish packaging is not configured.

Press Enter at the brief prompt to use the sample: an underwater exploration vehicle following fictional studio guidelines. The console displays tool requests, arguments, results, and per-round usage. Final totals include all model rounds, not just the last call. Missing usage is shown as unknown.

## Where to focus

| File | Responsibility |
| --- | --- |
| `ConceptStudio.Console/Program.cs` | One-line entry point invoking the console application. |
| `ConceptStudio.Console/ConsoleApp.cs` | Configuration, console input/output, timeout, and error presentation. |
| `ConceptStudio.Core/Requests/ConceptGenerationRequest.cs` | Fluent request recipe, LLM instructions, and validation of final concepts. |
| `ConceptStudio.Core/Requests/ConceptOutputSchema.cs` | JSON Schema sent to the model. |
| `ConceptStudio.Core/Agent/AgentRequestBuilder.cs` | Translates fluent calls into SDK options; performs no network calls. |
| `ConceptStudio.Core/Agent/AgentTool.cs` | Pairs a model-visible tool definition with its executable C# handler. |
| `ConceptStudio.Core/Agent/AgentRunner.cs` | Calls the model, executes requested tools, sends results back, and checks completion/refusal. |
| `ConceptStudio.Core/Tools/GuidelinesMcpClient.cs` | Starts the server process and initializes the MCP client connection. |
| `ConceptStudio.Core/Tools/McpToolAdapter.cs` | Discovers all server tools and adapts MCP calls to the existing agent loop. |
| `ConceptStudio.GuidelinesMcp/Program.cs` | Hosts the MCP server over stdio. |
| `ConceptStudio.GuidelinesMcp/DesignGuidelinesTools.cs` | The actual guideline lookup, exposed using MCP tool attributes. |
| `ConceptStudio.Core/Models/Concept.cs` | Plain application data records. |

The request recipe is:

```csharp
new AgentRequestBuilder()
    .WithModel(model)
    .WithInstructions(Instructions)
    .WithInput(brief)
    .WithOutput("concept_response", ConceptOutputSchema.Json)
    .WithTool(guidelinesTool)
    .WithMaxOutputTokens(1000)
    .Build();
```

`WithInput` appends user messages. Model, instructions, output, and token-limit methods replace their prior setting. `WithTool` adds a tool and rejects duplicate names. Output schema and tools are optional. Model and input are required. Each `Build()` produces fresh request options and fresh tool definitions; build a new request for every independent run. A built request is single-use and should not be run concurrently.

The builder checks that schemas are JSON objects. The provider checks its supported JSON Schema rules. `WithOutput` configures JSON generation; it does not deserialize the result automatically. `ReadConcepts` performs deserialization and application validation explicitly.

## Follow one MCP tool call

```text
Console app --initialize + tools/list--> Local MCP server process
Console app --task + discovered tool definition--> OpenAI
OpenAI --function call + theme argument--> Console app
AgentRunner --> MCP adapter --tools/call--> Local MCP server process
Local MCP server --guideline JSON--> Adapter --> AgentRunner
Console app --conversation + tool result--> OpenAI --> Final concepts
```

1. `GuidelinesMcpClient.ConnectAsync` starts the compiled server through `dotnet` and performs the MCP handshake. `await using` closes the client and server when the run finishes.
2. `DesignGuidelinesTool.CreateAsync` calls `ListToolsAsync`. The name, description, and argument schema are discovered from the server instead of duplicated in the app.
3. `ConceptGenerationRequest.Build` registers that discovered tool using `.WithTool(guidelinesTool)`.
4. The model returns a `FunctionCallResponseItem` with arguments such as `{"theme":"underwater"}`.
5. `AgentRunner.ExecuteToolAsync` invokes the adapter's handler as before.
6. The handler calls `client.CallToolAsync`, crossing the process boundary using MCP.
7. The SDK dispatches to `DesignGuidelinesTools.GetDesignGuidelines` in the server.
8. Text results are passed to the model. MCP `IsError` results become error JSON; connection/protocol failures stop the run and are reported by the console.
9. The runner appends the tool result using the original OpenAI `CallId` and calls the model again.

This is **application-managed MCP**: OpenAI requests a function; our app is the MCP client. OpenAI does not connect directly to the local server. No URL is needed for stdio. The server is a separate process, not an in-process function masquerading as MCP. An HTTP transport could be added later for a network-hosted server.

The server generates the MCP input schema from the method and its attributes. The adapter uses non-strict OpenAI function arguments to preserve that discovered schema, since arbitrary MCP schemas need not meet OpenAI's strict-schema rules. Server-side argument validation remains active. The final concept output schema still uses strict mode.

For debugging, put a breakpoint on `CallToolAsync` in the adapter. To break inside `GetDesignGuidelines`, attach a second .NET debugger to the `dotnet` process running `ConceptStudio.GuidelinesMcp.dll`; the app's debugger does not automatically attach to child processes.

The loop allows at most four model requests and shares one 60-second timeout. It checks completion and refusal before executing tools or reading output. Unknown tools and malformed arguments become tool error results. Cancellation and unexpected handler failures propagate; this learning app does not silently retry tool side effects. Stored response retrieval is disabled; the runner explicitly sends conversation history. This is not a blanket guarantee about provider data retention.

## Experiment

1. Generate an underwater vehicle following studio guidelines.
2. Inspect whether both descriptions and image prompts include two circular observation windows and a rescue beacon.
3. Change a guideline in `ConceptStudio.GuidelinesMcp/DesignGuidelinesTools.cs` and repeat. The changed data should influence the result.
4. Ask for an unsupported theme such as space. Inspect the error result and whether the model invents guidelines.
5. Remove `.WithTool(...)` to compare output without retrieved data. Also adjust the instructions if you no longer want the model to retrieve guidelines.

This now uses a local MCP server through the official C# SDK. Image previews and feedback-driven image edits are available after concept selection. A separate web UI is available (see below). Trace output is intended for these fictional learning inputs; review what you log before using private data.

## Local integration checks

```sh
dotnet run --project ConceptStudio.Checks
```

Run the command above from the repository root. These checks use no API key and make no model requests. They start a real MCP server subprocess and verify discovery, dispatch using model-shaped arguments, malformed arguments, unsupported themes, unknown tools, cancellation, and concept validation, alongside the existing builder checks. They do not verify live model behaviour or OpenAI acceptance of the discovered schema. An optional command-line argument can specify another built server DLL.

The server logs only to stderr because stdout carries MCP JSON-RPC messages. It inherits only basic process environment variables, not the OpenAI API key.

## References

- https://developers.openai.com/api/docs/guides/function-calling
- https://developers.openai.com/api/docs/guides/structured-outputs
- https://developers.openai.com/api/docs/guides/tools-connectors-mcp

- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk

## Image previews

After generating three concepts, choose 1–3 to generate one image, or Enter to skip.
The image step uses the Images API directly: it is a fixed workflow following the agent loop, not an MCP tool.

- `Images/ConceptImageGenerator.cs`: builds the visual prompt, calls `ImageClient.GenerateImageAsync`, and saves the decoded PNG.
- `Images/ImagePreviewFlow.cs`: concept selection, feedback loop, image client configuration, timeout, and errors.
- `Images/ConceptImageEditor.cs`: uploads the latest PNG and feedback using `ImageClient.GenerateImageEditAsync`, then saves a new revision.
- Default model: `gpt-image-2.5-flare`. Override with `OPENAI_IMAGE_MODEL` (use a compatible GPT Image model).
- Settings: 1024×1024, low quality, PNG, one image. The image call is billed separately from text generation.
- Output: `artifacts/images/<timestamp-and-id>/preview.png` beneath the current working directory. Override the base image directory with `CONCEPT_STUDIO_OUTPUT_DIR`.
- `request.json` beside each image contains the selected concept, exact prompt, model, and settings. It is saved before the request and remains if generation fails.
- The image model receives the selected concept, description, image prompt, and model-reported applied guidelines; it does not automatically see the agent's MCP history.
- The image operation has a fresh three-minute timeout. SDK retries are disabled; a timeout does not prove the provider did no work.
- These are concept visualizations, not validated construction instructions. Check whether the image preserves the requested colours, window count, and other design constraints.

The text token totals do not include image generation. Check API usage/billing for image costs. Model access may require organisation verification.

The check project uses a fake HTTP response to verify the actual SDK image request, image decoding/saving, prompt capture, error propagation, and skipping. It makes no paid image calls. A live run is still required to confirm model access and evaluate visual quality.

Image API documentation: https://developers.openai.com/api/docs/guides/image-generation

### Refine a preview

After generation, open the saved PNG and enter feedback at the console prompt, for example:
`Remove the third window; keep exactly two circular observation windows. Preserve everything else.`
Press Enter without feedback to finish. Each submitted edit makes a separate paid image API call.

The app uploads the latest successful PNG to `/v1/images/edits` alongside the feedback and applied guidelines.
It asks the model to preserve unrelated details; the designer's explicit feedback takes precedence over conflicting guidelines.
Each revision gets a separate folder containing `preview.png` and `request.json` with the source image path,
feedback, prompt, concept and settings. Previous images remain available. Each request has a fresh three-minute
timeout and no automatic retries. An error ends the image flow, leaving prior revisions intact.
This loop keeps context through the latest image; it does not resend the full history of earlier feedback.
Review each revision: precise edits and preservation of unrelated details are not guaranteed.

## Multiple MCP tools

The local server exposes `get_design_guidelines(theme)` and `get_colour_palette(paletteName)`.
`ColourPaletteTools.cs` contains fictional local palettes (`ocean-rescue` and `sunset`);
this method is where a real database or external API lookup could be added.
`McpToolAdapter.DiscoverAsync` discovers all tools on this trusted local server and creates
one agent handler per tool. `ConsoleApp` registers those tools with the request builder.
The model chooses calls based on the brief, instructions, and discovered descriptions/schemas.
`AgentRunner` routes each call by name through MCP and returns its result to the model.

Try these briefs and inspect the tool-call trace:

- Guidelines: `An underwater exploration vehicle. Follow studio guidelines.`
- Palette: `An abstract sculpture using the studio sunset palette. No specific design theme.`
- Both: `An underwater exploration vehicle using the studio ocean-rescue palette. Follow studio guidelines.`
- Conflict: `An underwater exploration vehicle using the studio sunset palette. Follow studio guidelines.`
- Lookup error: `An abstract sculpture using the studio moonlight palette. No specific design theme.`

The instructions request palette retrieval only for named palettes, and put retrieved names/hex
codes into image prompts. An explicitly requested palette takes precedence over conflicting
guideline colours; the model should explain the conflict and omit that unapplied colour guideline.
Tool choice and visual colour fidelity still need live evaluation. Local checks verify discovery
and routing through the real MCP server without making model requests.

## Advisory vision review

After saving each generated or edited PNG, the app prints its path and makes a separate paid
Responses API call with the PNG bytes and the concept's applied guidelines. No guidelines means
no review call. `OPENAI_REVIEW_MODEL` overrides the default `gpt-4.1-mini`; choose a model supporting
image input and structured output. Review has its own 60-second timeout and no automatic retries.

- `Images/ConceptImageReviewer.cs`: image/text request, strict output schema, validation, and `review.json` persistence.
- `Images/ImageReviewFlow.cs`: console findings and recoverable review errors.
- Every guideline has one numbered finding: `pass`, `fail`, or `uncertain`, with visible evidence.
- The app saves model, image path, guideline list, findings and review token usage in `review.json` beside the PNG.
- Findings are advisory: failures never delete images or trigger regeneration, and unavailable reviews do not prevent editing.
- Reviews use the original **model-reported applied guidelines**, not an independent copy of all MCP requirements.
  After a deliberate guideline-changing edit, a finding may therefore describe an intentional difference.
- Review costs are separate from the console's concept-generation token totals. Review results can be wrong;
  compare them against your own inspection, particularly counts and occluded details.

The fake-HTTP checks verify the image payload and result handling, not visual judgement or live model access.
API reference: https://developers.openai.com/api/docs/guides/images-vision

## Web interface (console remains available)

Run from the repository root, in a terminal with `OPENAI_API_KEY` configured:

```sh
dotnet run --project ConceptStudio.Web
```

Open http://127.0.0.1:5080. Enter a brief, choose a concept, generate an image, review its
findings, then submit edit feedback. You can select earlier revisions and edit from them.
The image appears before the separate vision review completes; failed reviews leave editing
available and can be retried explicitly. Concept generation, image generation/editing, and
vision reviews make paid API calls. No retries occur automatically.

The UI uses plain HTML/CSS/JavaScript served by ASP.NET Core: no Node/npm setup is needed.
The API key is read by the server only and never sent to the browser. This is a local single-user
learning app bound to loopback, without accounts or production deployment configuration.
Unsaved work is held in server memory. Use Save session to persist a draft or completed work,
then reopen it from Your saved designs after a restart. Refreshing restores the current session
while the server remains running; after restart it restores the last saved snapshot.
Images, prompts, and review files remain on disk in `ConceptStudio.Web/artifacts/images` by default.
`CONCEPT_STUDIO_OUTPUT_DIR` overrides that location. Browser input never specifies filesystem paths.

Code layout:

- `ConceptStudio.Core`: shared agent loop, request builders, models, MCP adapter and image services.
  Existing prompts (including the three-view image prompt) are preserved.
- `ConceptStudio.Console`: original console entry point and console interaction flows.
- `ConceptStudio.Web/Program.cs`: local HTTP endpoints, validation and safe error responses.
- `ConceptStudio.Web/StudioService.cs`: orchestrates shared services and holds web-session state.
- `ConceptStudio.Web/wwwroot`: browser UI, styles, and fetch calls.
- `ConceptStudio.GuidelinesMcp`: the separate MCP server, built/copied by either host.

The console still runs exactly as before:

```sh
dotnet run --project ConceptStudio.Console
```

Existing `OPENAI_MODEL`, `OPENAI_IMAGE_MODEL`, and `OPENAI_REVIEW_MODEL` overrides work in both hosts.
An API key configured only in the console's launch settings will not carry over to the web host;
use the environment of the terminal launching the web app.

## Saving design sessions

Use **New session** for a fresh brief, **Save session** to write the current design, and
**Your saved designs → Open** to reopen the last saved snapshot. Saving and opening require no
API key. You can save an empty session or a brief before generating concepts. Unsaved changes
are marked, and switching sessions or leaving the page warns before discarding them.

Saved data includes the session name, brief, concepts and their applied guidelines, MCP trace,
all image revisions, parent revision IDs, edit feedback, vision findings, and the selected revision.
Use **Concepts to add** to generate 1, 2, or 3 concepts from the current brief. Each run appends
concepts to the same session, retaining prior work and image associations. Each new concept stores
the request brief directly in application code; **Original brief** on its card shows that text.
Changing the draft brief does not change existing concepts. Older saves remain readable, but
concepts created before this feature have no recorded original brief (we do not guess it from the
session's latest draft). The console retains its default of three concepts.
Reviews continue to assess each concept's original applied guidelines.

`SessionStore.cs` writes a versioned `session.json` into
`ConceptStudio.Web/artifacts/sessions/<session-id>/`, alongside session-owned image copies at
`images/<revision-id>/preview.png`. Set `CONCEPT_STUDIO_SESSIONS_DIR` to override that root.
The JSON snapshot is replaced only after all images and the complete JSON have been written;
a failed save leaves the previous snapshot intact. Original generation request files remain
in the image artifacts directory. Session saves contain their concept data and revision feedback.
No API credentials are written to session files.

This is explicit local file persistence, not OpenAI conversation memory. New work after a save
needs another Save; restarting the server discards unsaved changes. There is no database or cloud
sync yet. The console still works as before. Checks exercise draft saves, full revision round trips,
independent image copies, renaming, and preserving the previous snapshot when a save fails.

Each concept card has **Delete concept**. Confirming removes that concept and its revisions/reviews
from the active session. Use **Save session** to persist the deletion; reopening the last save
before saving restores it. Other concepts keep their images and briefs. Existing image files
remain on disk as artifacts; deletion removes them from the session, not from the filesystem.

## Sketch and reference inputs

The web brief accepts one optional PNG/JPEG reference (up to 5 MB) per generation. Upload a
reference, choose **Preserve the main shape** or **Use as loose inspiration**, then add concepts.
The selector can reuse earlier references or choose **No reference image** for a new idea.
Uploading is local and makes no model call. Generating concepts and visualizing them sends the
selected image to OpenAI in addition to the text and uses the normal paid API calls.

- `AgentRequestBuilder.WithInput(text, image)` creates a multimodal user message.
- `ConceptGenerationRequest` supplies the brief, reference guidance and image bytes to Responses.
- `StudioService` records the reference ID and mode on each resulting concept, independently of
  the next draft's settings. The model does not choose that provenance.
- `ConceptImageGenerator` uses image edits for concepts with a reference, passing the original
  pixels plus the concept prompt and guidelines. Text-only concepts still use image generation.
- Subsequent refinements edit the selected generated revision as before.

This follows the [OpenAI image edit API](https://developers.openai.com/api/reference/resources/images/methods/edit).
Preserving shape is a prompt preference, not an exact geometry guarantee.

Use **Save session** to persist reference metadata and selection. Original files are stored under
`artifacts/sessions/<session-id>/references/` with server-generated filenames; the session JSON
stores IDs and display names, not client file paths. Existing sessions without reference fields
remain compatible. The original reference appears inside each concept's **Original brief** panel.
Removing it from the next draft does not remove it from existing concepts. PDFs and Word documents
are not supported by this first version.

Local checks cover multimodal request payloads, reference-based image edits, saving/reopening,
per-concept provenance, invalid formats and upload size limits; model responses are simulated.

## MCP over HTTP: a separately running web service

The solution includes `ConceptStudio.GuidelinesMcp.Web`, an ASP.NET Core host for the **same**
guidelines and palette tools. Its `Program.cs` registers HTTP transport and maps `/mcp`.
This uses the SDK's [Streamable HTTP transport](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md).
The existing stdio server remains the default.

From the solution directory, start the service in one terminal:

```sh
dotnet run --project ConceptStudio.GuidelinesMcp.Web
```

In another terminal, discover and call its tools directly (no API key or model required):

```sh
dotnet run --project ConceptStudio.Checks -- --mcp-http
```

Read `ConceptStudio.Checks/HttpMcpDemo.cs`: `ListToolsAsync` discovers tool definitions and
`CallToolAsync` invokes `get_design_guidelines` with `theme = underwater`, then the sunset palette.
The server is an independent process; the HTTP client does not launch or stop it.

To use HTTP MCP within the actual web app, keep the MCP service running and start the app with:

```sh
CONCEPT_STUDIO_MCP_URL=http://127.0.0.1:5090/mcp dotnet run --project ConceptStudio.Web
```

The same environment setting works with `ConceptStudio.Console`. For a VS Code debug run, put
`CONCEPT_STUDIO_MCP_URL` in that debug profile's environment. Omit/unset it to use stdio again.
Save existing work before restarting the app. A configured but unavailable HTTP endpoint produces
an error; it does not silently fall back to stdio.

Follow the flow:

```text
StudioService / ConsoleApp
  -> GuidelinesMcpClient.ConnectAsync (chooses HTTP when configured)
  -> HttpClientTransport -> http://127.0.0.1:5090/mcp
  -> McpToolAdapter.DiscoverAsync -> tools/list
  -> agent loop receives a model tool request
  -> adapter calls CallToolAsync -> tools/call over HTTP
  -> DesignGuidelinesTools or ColourPaletteTools executes in the web service
  -> result returns to the app, then to the model
```

`MapMcp` implements the MCP protocol endpoint; we do not write one REST controller per tool.
HTTP transport is stateless here because these tools are simple lookups. This transport setting
is separate from design-session persistence and model conversation state.

The demo host binds to loopback and validates Host/Origin. It has no authentication and is intended
for local learning. Deploying it for a team would also need HTTPS and authentication/authorization.

To delete a whole saved session, select it in **Your saved designs** and click **Delete**.
Confirmation permanently removes its saved session folder, including owned images and uploaded
references. This happens immediately, without a separate Save. The active view is cleared if it
was that session; other sessions are preserved. Original generation artifacts outside the session
folder are retained. Busy sessions must finish before deletion.
