using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace BibleEcho.Workspace;

public sealed class WorkspaceError(int status, string message) : Exception(message) {
    public int Status { get; } = status;
}

public sealed class Store(HttpClient http, IConfiguration config) {
    const string Table = "rest/v1/echo_entries";
    const string Columns = "id,kind,body,revision,created_at,updated_at";
    async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? content, CancellationToken ct) {
        if (!Uri.TryCreate(config["SUPABASE_URL"],UriKind.Absolute,out var origin) || !(origin.Scheme=="https" || (origin.Scheme=="http" && origin.IsLoopback)) ||
            string.IsNullOrWhiteSpace(config["SUPABASE_SERVICE_ROLE_KEY"]))
            throw new WorkspaceError(503,"Configure Supabase on the API server.");
        using var request = new HttpRequestMessage(method,new Uri(origin,path)) { Content=content };
        var key=config["SUPABASE_SERVICE_ROLE_KEY"];
        request.Headers.Add("apikey",key);
        if(!key!.StartsWith("sb_secret_",StringComparison.Ordinal))
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Headers.TryAddWithoutValidation("Prefer","return=representation");
        var response=await http.SendAsync(request,ct);
        if (response.IsSuccessStatusCode) return response;
        var conflict=(int)response.StatusCode==409;
        response.Dispose();
        throw new WorkspaceError(conflict?409:502,conflict?"Another service or operator already owns this output.":"Supabase request failed. Check the migration, bucket and server credentials.");
    }
    async Task<JsonArray> Rows(HttpMethod method,string query,object? value,CancellationToken ct) {
        using var response=await Send(method,Table+"?"+query,value==null?null:JsonContent.Create(value),ct);
        return (await response.Content.ReadFromJsonAsync<JsonArray>(ct))!;
    }
    public async Task<JsonArray> List(string kind,CancellationToken ct) {
        var all=new JsonArray();
        for(int offset=0;;offset+=100){
            var rows=await Rows(HttpMethod.Get,$"select={Columns}&kind=eq.{kind}&order=created_at.desc,id&limit=100&offset={offset}",null,ct);
            foreach(var row in rows) all.Add(row!.DeepClone());
            if(rows.Count<100)return all;
        }
    }
    public async Task<JsonObject> Get(string kind,Guid id,CancellationToken ct) =>
        (await Rows(HttpMethod.Get,$"id=eq.{id}&kind=eq.{kind}",null,ct)).FirstOrDefault()?.AsObject()
        ?? throw new WorkspaceError(404,"This entry no longer exists.");
    public async Task<JsonNode> Insert(string kind,JsonObject body,CancellationToken ct) =>
        (await Rows(HttpMethod.Post,"select="+Columns,new {kind,body},ct))[0]!;
    public async Task<JsonNode> Update(string kind,Guid id,int revision,JsonObject body,string? owner,CancellationToken ct) =>
        (await Rows(HttpMethod.Patch,$"id=eq.{id}&kind=eq.{kind}&revision=eq.{revision}&select={Columns}",
            new {body,revision=revision+1,owner_hash=owner,updated_at=DateTimeOffset.UtcNow},ct)).FirstOrDefault()
        ?? throw new WorkspaceError(409,"Entry changed. Refresh before trying again.");
    public async Task Delete(string kind,Guid id,int revision,CancellationToken ct) {
        if((await Rows(HttpMethod.Delete,$"id=eq.{id}&kind=eq.{kind}&revision=eq.{revision}",null,ct)).Count==0)
            throw new WorkspaceError(409,"Entry changed. Refresh before deleting.");
    }
    const string Bucket="storage/v1/object/echo-media/";
    public async Task Upload(Guid key,byte[] bytes,string mime,CancellationToken ct) {
        using var content=new ByteArrayContent(bytes);
        content.Headers.ContentType=new MediaTypeHeaderValue(mime);
        using var response=await Send(HttpMethod.Post,Bucket+key,content,ct);
    }
    public async Task<byte[]> Download(Guid key,CancellationToken ct) {
        using var response=await Send(HttpMethod.Get,Bucket+key,null,ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    public async Task Remove(Guid key,CancellationToken ct) {
        using var response=await Send(HttpMethod.Delete,"storage/v1/object/echo-media",JsonContent.Create(new {prefixes=new[]{key.ToString()}}),ct);
    }
}
