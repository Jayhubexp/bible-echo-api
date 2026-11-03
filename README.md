# Bible Echo API
An independent .NET 10 web service for Bible Echo. The Docker image contains a read-only KJV and its FTS5 index. No disk or database service is needed.

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
| POST | /transcribe | audio/wav binary, canonical 44-byte RIFF header, mono PCM16 at 16 or 24 kHz | transcript |
| POST | /backgrounds/generate | JSON with prompt (1–1500 characters) | image as data URL or temporary signed HTTPS URL |

Spoken mode only returns explicit citations resolved against the KJV. Manual lookup also supports the desktop full-text matcher. Each verse has book, chapter, verse, text, version and formattedReference. Bad input returns 400/415, oversized bodies 413, missing credentials 503, model failures 502/429 and upstream timeouts 504. Requests are stateless. CORS governs browser origins. The deployed model routes are public and consume the server account's quota. Audio, prompts and credentials are excluded from application request logs.

## Migration inventory
The original BibleEcho.slnx contains net10.0 projects. The desktop UI uses Avalonia Desktop 12.1.4 and CommunityToolkit.Mvvm 8.4.2. BibleEcho.Audio uses Windows WASAPI via NAudio. BibleEcho.AI also references Whisper.net. The original API combines scripture search, optional Ollama and SignalR. The desktop library catalog stores songs and local picture paths. The desktop projector presents lyrics, scripture and stills in a separate Avalonia window. Video playback was outside the original implementation.

Reused from the desktop tree, inside this repository:
- data/bible.db, containing the KJV and its populated search index.
- Scripture/BibleVerse.cs, ScriptureReference.cs, ScriptureMatchResult.cs and IScriptureMatcher.cs from BibleEcho.Core.
- Scripture/ScriptureMatchingEngine.cs and SpokenCitationLexer.cs from BibleEcho.AI. The lexer accepts compound spoken numbers and punctuation before verse markers.
- Scripture/SqliteBibleRepository.cs and IBibleRepository.cs. The web copy uses read-only connections and omits schema creation.

Program.cs adapts the original search flow to the versioned HTTP contract. MetaClient.cs handles remote inference. BibleEcho.Api.csproj, Dockerfile, .dockerignore, .gitignore, .env.example, render.yaml and this README complete the service. The historical Flask server in api/server.py offered SQLite lookup and loaded local Whisper. Reusing the current C# implementation avoids porting the parser or deploying local weights.

[Meta speech-to-text format](https://dev.meta.ai/docs/speech-to-text) · [Meta image generation format](https://dev.meta.ai/docs/image-generation)
