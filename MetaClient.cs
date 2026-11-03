using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed record BackgroundRequest(string? Prompt);
public sealed class MetaFailure(int status,string message) : Exception(message) {
    public int Status { get; } = status;
}
public sealed class MetaClient(HttpClient client, IConfiguration config, IHttpClientFactory clients) {
    async Task<JsonElement> Send(string path,HttpContent content,CancellationToken ct) {
        using var request = new HttpRequestMessage(HttpMethod.Post,path) {Content=content};
        var key = config["MODEL_API_KEY"];
        if (string.IsNullOrWhiteSpace(key)) throw new MetaFailure(503,"Configure MODEL_API_KEY on the API server.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",key);
        using var response = await client.SendAsync(request,ct);
        if (!response.IsSuccessStatusCode) {
            var code = (int)response.StatusCode;
            throw new MetaFailure(code == 429 ? 429 : 502,$"Meta declined the request (HTTP {code}).");
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
    }
    public async Task<System.Text.Json.Nodes.JsonObject> Draft(string instructions,object input,CancellationToken ct) {
        using var body=JsonContent.Create(new {model="muse-spark-1.3",response_format=new {type="json_object"},
            messages=new[]{new {role="developer",content=instructions},new {role="user",content=JsonSerializer.Serialize(input)}}});
        var json=await Send("chat/completions",body,ct);
        try {
            return System.Text.Json.Nodes.JsonNode.Parse(json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!)!.AsObject();
        } catch(Exception e) when(e is not OperationCanceledException) {
            throw new MetaFailure(502,"Meta returned an unreadable draft.");
        }
    }
    public static async Task<IResult> Transcribe(HttpRequest request,MetaClient meta,BibleEcho.Core.Interfaces.IBibleRepository repo,bool? detect,string? preceding,CancellationToken ct) {
        if (preceding?.Length>300) return Results.BadRequest(new {error="Context exceeds 300 characters."});
        if (request.ContentType != "audio/wav") return Results.Json(new {error="Send PCM audio/wav."},statusCode:415);
        using var stream = new MemoryStream();
        await request.Body.CopyToAsync(stream,ct);
        var bytes = stream.ToArray();
        if (!ValidWav(bytes)) return Results.BadRequest(new {error="Expected a canonical mono PCM16 WAV at 16 or 24 kHz."});
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("""{"model":"muse-voice-transcribe-1.0","mode":"PUSH_TO_TALK","audioEncoding":"WAV","keywords":["Matthew","Mark","Luke","John","chapter","verse"]}""",Encoding.UTF8,"application/json"),"request");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file,"audio","sermon.wav");
        var json = await meta.Send("asr/transcribe",form,ct);
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("transcript",out var value) || value.ValueKind != JsonValueKind.String)
            throw new MetaFailure(502,"Meta returned no transcript field.");
        var transcript=value.GetString()!;
        if(detect!=true)return Results.Ok(new {transcript});
        var matches=await BibleEcho.AI.CitationLookup.Resolve(transcript.Length>2000?transcript[^2000..]:transcript,preceding,true,repo,ct);
        return Results.Ok(new {transcript,matches.Passages,matches.Unresolved});
    }
    static bool ValidWav(byte[] b) {
        if (b.Length <= 44 || b.Length % 2 != 0) return false;
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset));
        int S(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset));
        return b.AsSpan(0,4).SequenceEqual("RIFF"u8) && I(4)==b.Length-8 &&
            b.AsSpan(8,8).SequenceEqual("WAVEfmt "u8) && I(16)==16 && S(20)==1 && S(22)==1 &&
            I(24) is 16000 or 24000 && I(28)==I(24)*2 && S(32)==2 && S(34)==16 &&
            b.AsSpan(36,4).SequenceEqual("data"u8) && I(40)==b.Length-44;
    }
    HttpClient ImageDownloader()=>clients.CreateClient("generated-image");
    public static async Task<IResult> Generate(BackgroundRequest input, MetaClient meta,CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length>1500)
            return Results.BadRequest(new {error="Enter a prompt between 1 and 1,500 characters."});
        using var body = JsonContent.Create(new {model="muse-image-1.0",prompt=input.Prompt.Trim(),size="1536x1024",output_format="webp",response_format="b64_json"});
        var json = await meta.Send("images/generations",body,ct);
        if (json.ValueKind==JsonValueKind.Object && json.TryGetProperty("data",out var data) && data.ValueKind==JsonValueKind.Array && data.GetArrayLength()>0 && data[0].ValueKind==JsonValueKind.Object) {
            var image=data[0];
            if (image.TryGetProperty("b64_json",out var encoded) && encoded.ValueKind==JsonValueKind.String && encoded.GetString() is {Length:>0} b64)
                return Results.Ok(new {image=$"data:image/webp;base64,{b64}"});
            if (image.TryGetProperty("url",out var url) && url.ValueKind==JsonValueKind.String && Uri.TryCreate(url.GetString(),UriKind.Absolute,out var uri) && uri.Scheme=="https")
                {
                // Only download the URL returned by the authenticated model request.
                // This client has no Meta authorization header and never follows redirects.
                using var download=meta.ImageDownloader();
                try {
                    using var result=await download.GetAsync(uri,ct);
                    if(result.IsSuccessStatusCode){
                        var bytes=await result.Content.ReadAsByteArrayAsync(ct);
                        var mime=BibleEcho.Workspace.Endpoints.Mime(bytes);
                        if(mime.StartsWith("image/"))return Results.Ok(new {image=$"data:{mime};base64,{Convert.ToBase64String(bytes)}"});
                    }
                }catch(Exception e) when(!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or BibleEcho.Workspace.WorkspaceError) { }
                return Results.Ok(new {image=uri.AbsoluteUri});
            }
        }
        throw new MetaFailure(502,"Meta returned no usable background.");
    }
}
