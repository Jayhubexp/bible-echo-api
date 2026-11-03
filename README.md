# Bible Echo API
An independent .NET 10 web service for Bible Echo. The Docker image contains a read-only KJV and its FTS5 index. Scripture lookup reads the bundled database. The shared media, song and service libraries use Supabase.

## Render
Connect this repository to a Render Web Service and choose Docker, or create a Blueprint using render.yaml. Use the repository root, main branch, ./Dockerfile and health path /api/health. Set MODEL_API_KEY as a Render secret with access to both Muse models. Set FRONTEND_ORIGIN to the exact Vercel origin, including https:// and excluding any trailing slash. Multiple trusted origins can be comma-separated. The process listens on 0.0.0.0 using Render's PORT. A sleeping free instance can delay the first request. Visit /api/health before the service.

## Local run
Install .NET SDK 10 and run `dotnet run` from this folder. Defaults are port 5080 and browser origin http://localhost:5173. Set environment variables in the launching shell. For the container, copy .env.example to .env, fill in its values and run:

```sh
docker build -t bible-echo-api .
docker run --rm --env-file .env -p 5080:5080 bible-echo-api
```
Plain dotnet run reads process environment variables. The Docker --env-file flag loads a local .env file.

## HTTP v1
Routes are available under /api/v1 and the short /api alias. Responses are JSON. Request bodies are limited to 512,000 bytes.

| Method | Route under prefix | Input | Result |
|---|---|---|---|
| GET | /health | none | status and version |
| GET | /scripture/search | q, optional spoken=true and preceding context | passages with reference and ordered KJV verses, empty array on no match |
| POST | /transcribe | audio/wav binary, canonical 44-byte RIFF header, mono PCM16 at 16 or 24 kHz | transcript; with `?detect=true`, also passages and unresolved reference messages. Optional `preceding` accepts up to 300 characters of prior transcript. |
| POST | /backgrounds/generate | JSON with prompt (1–1500 characters) | image as data URL or temporary signed HTTPS URL |

Spoken mode only returns explicit citations resolved against the KJV. Manual lookup also supports the desktop full-text matcher. Each verse has book, chapter, verse, text, version and formattedReference. Bad input returns 400/415, oversized bodies 413, missing credentials 503, model failures 502/429 and upstream timeouts 504. The original scripture, transcription and generation routes are stateless. CORS governs browser origins. The deployed model routes are public and consume the server account's quota. Audio, prompts and credentials are excluded from application request logs.

## Migration inventory
The original BibleEcho.slnx contains net10.0 projects. The desktop UI uses Avalonia Desktop 12.1.4 and CommunityToolkit.Mvvm 8.4.2. BibleEcho.Audio uses Windows WASAPI via NAudio. BibleEcho.AI also references Whisper.net. The original API combines scripture search, optional Ollama and SignalR. The desktop library catalog stores songs and local picture paths. The desktop projector presents lyrics, scripture and stills in a separate Avalonia window. Video playback was outside the original implementation.

Reused from the desktop tree, inside this repository:
- data/bible.db, containing the KJV and its populated search index.
- Scripture/BibleVerse.cs, ScriptureReference.cs, ScriptureMatchResult.cs and IScriptureMatcher.cs from BibleEcho.Core.
- Scripture/ScriptureMatchingEngine.cs and SpokenCitationLexer.cs from BibleEcho.AI. The lexer accepts compound spoken numbers and punctuation before verse markers.
- Scripture/SqliteBibleRepository.cs and IBibleRepository.cs. The web copy uses read-only connections and omits schema creation.

Program.cs adapts the original search flow to the versioned HTTP contract. MetaClient.cs handles remote inference. BibleEcho.Api.csproj, Dockerfile, .dockerignore, .gitignore, .env.example, render.yaml and this README complete the service. The historical Flask server in api/server.py offered SQLite lookup and loaded local Whisper. Reusing the current C# implementation avoids porting the parser or deploying local weights.

[Meta speech-to-text format](https://dev.meta.ai/docs/speech-to-text) · [Meta image generation format](https://dev.meta.ai/docs/image-generation)


## Configure the shared workspace

1. Create a Supabase project for the church workspace. In its SQL Editor, execute `migrations/001_workspace.sql`. It creates the PostgreSQL table, access grants, single-controller constraint and private `echo-media` bucket. Enable the Data API for the public schema. The API uses PostgREST over HTTPS, so it does not require a direct PostgreSQL connection string.
2. Set the following secrets on the existing Render API service. Restart with the new environment. The original desk routes can start without the workspace configuration.

| Render variable | Value |
| --- | --- |
| SUPABASE_URL | Project API origin, such as https://PROJECT.supabase.co |
| SUPABASE_SERVICE_ROLE_KEY | Server-side secret key (sb_secret_...) or legacy service_role JWT from that project |
| WORKSPACE_OPERATOR_KEY | A long random passphrase shared with trusted media operators |
| MODEL_API_KEY | Existing Meta server credential with access to Muse Image, Voice and Spark 1.3 |
| FRONTEND_ORIGIN | Exact HTTPS Vercel origin, or a comma-separated allowlist |

3. Deploy this API repository with its existing Dockerfile. Check /api/health, then unlock the workspace from the Vercel desk. A green health check verifies API startup and the bundled KJV. Unlocking additionally exercises PostgreSQL. Upload and download a small test image to exercise the bucket.
4. Rotate the operator passphrase on Render when access changes. All holders can read and edit the same church library and explicitly recover an abandoned projector. There are no individual accounts. Database rows and private bucket credentials are inaccessible to the anonymous frontend role under the supplied migration. Use a dedicated project or review existing Storage policies for grants covering this bucket.
5. Before using the system at a service, save a named image, refresh the desk, restart the API, then compare the downloaded file. Test an armed rehearsal with a short announcement, one scripture cue and a licensed song. Check Pause, Resume, Clear and Stop on the venue display.

New API routes live under /api/v1/workspace. All require X-Operator-Key. Service commands also use X-Controller, a random per-desk token whose hash is stored server-side. Media and song libraries support list, create, revision-checked update and delete. Media downloads require the operator header. Service edits return to Draft and require explicit approval. Concurrent writes fail with 409. A database uniqueness constraint permits one armed service per shared workspace.

Uploads are limited to 32 MiB, checked by file signature, stored under server-generated object keys and downloaded with an allowlisted MIME type. The API stores the original prompt, timestamp, pixel dimensions, tags and presentation settings with the media record. Deletion marks the row pending before removing the object. Refresh and retry Delete if interrupted. The API reserves metadata before uploading. Interrupted transfers appear as pending and can be removed after three minutes, then retried. Configure Supabase backups and any media backup separately for the retention period your team needs.

## Run controller and hosting

The open operator desk drives execution. Arming preloads a reviewed plan and obtains ownership. PostgreSQL stores absolute UTC deadlines, status, item, actual item start and remaining time. The API resolves the IANA timezone, rejects ambiguous local starts, and uses server UTC for transitions. A pause freezes the remainder. A late tick (over ten seconds), missing media, or an API error requires operator attention. After an outage use Refresh run state, then Resume. After a reload use Recover paused control. Recovery preloads media, checks the revision and transfers ownership in a paused state. A second operator must explicitly recover or release another controller. An abandoned run stays reserved until an operator acts.

Render free instances sleep when idle. Keep the operator browser and audience display open, disable system sleep and warm the API before arming. A sleeping browser, closed window or lost connection cannot advance a service reliably. If independent wall-clock dispatch is required, use an always-on worker with PostgreSQL ownership and a connected presentation client. This release uses an armed browser controller. Microphone activation and fullscreen require separate user gestures.

## Tests and model references

Run `dotnet run --project tests/WorkspaceChecks.csproj -c Release`. The dependency-free checks cover UTC/DST, run transitions, timing, manual override, MIME validation, song arrangement persistence and validated assistant responses using a model transport fixture. The frontend repository includes projector and shared-workspace browser checks.

[Meta chat completions](https://dev.meta.ai/docs/cookbook/quickstart-chat-completions) · [Meta image generation](https://dev.meta.ai/docs/image-generation) · [Supabase private storage](https://supabase.com/docs/guides/storage/buckets/fundamentals) · [Render free hosting](https://render.com/docs/free)

### Local verification on 3 November 2026

The backend check runner passed all 25 checks. The Release build and Docker publish succeeded. A fresh container passed its health check and returned John 3:16 from the bundled KJV.

The migration ran against a disposable local Supabase stack. Anonymous access to the workspace table was denied. A saved image downloaded byte-for-byte identically after restarting the API process, and a saved song retained its lyrics. Real Muse Spark requests returned a validated formatting draft and a four-item service outline. These results cover the local deployment. Provision and rehearse the hosted deployment separately.
