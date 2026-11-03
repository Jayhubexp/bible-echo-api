using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace BibleEcho.Workspace;

public static class Endpoints {
    public const int MaxFile=32*1024*1024;
    public static void Configure(WebApplicationBuilder builder){
        builder.Services.AddHttpClient<Store>(c=>c.MaxResponseContentBufferSize=MaxFile+1024*1024);
        builder.Services.AddRateLimiter(options=>{
            options.RejectionStatusCode=429;
            options.AddPolicy("model",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",
                _=>new FixedWindowRateLimiterOptions{PermitLimit=30,Window=TimeSpan.FromMinutes(1)}));
            options.AddPolicy("workspace",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",
                _=>new FixedWindowRateLimiterOptions{PermitLimit=180,Window=TimeSpan.FromMinutes(1)}));
        });
    }
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static string Controller(HttpRequest req){
        var token=req.Headers["X-Controller"].ToString();
        Rules.Need(Guid.TryParse(token,out _),"A controller session is required.");
        return Hash(token);
    }
    public static void Map(WebApplication app){
        var group=app.MapGroup("/api/v1/workspace").RequireRateLimiting("workspace");
        group.AddEndpointFilter(async(context,next)=>{
            var ctx=context.HttpContext;
            var secret=ctx.RequestServices.GetRequiredService<IConfiguration>()["WORKSPACE_OPERATOR_KEY"];
            if(string.IsNullOrWhiteSpace(secret))throw new WorkspaceError(503,"Workspace operator access has not been configured.");
            if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(secret)),Convert.FromHexString(Hash(ctx.Request.Headers["X-Operator-Key"].ToString()))))
                throw new WorkspaceError(401,"Enter the shared workspace passphrase.");
            ctx.Response.Headers.CacheControl="no-store";
            ctx.Response.OnStarting(()=>{ctx.Response.Headers["X-Echo-Time"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();return Task.CompletedTask;});
            return await next(context);
        });
        group.MapGet("/{kind}",(string kind,Store db,CancellationToken ct)=>{
            Rules.Need(kind is "media" or "songs" or "services","Unknown collection.");
            return db.List(kind,ct);
        });
        group.MapPost("/media",Upload);
        group.MapGet("/media/{id:guid}/file",async(Guid id,Store db,CancellationToken ct)=>{
            var row=await db.Get("media",id,ct);var body=row["body"]!;
            Rules.Need(body["pending"]==null && body["deleting"]==null,"This background is unavailable.");
            return Results.File(await db.Download(Guid.Parse((string)body["key"]!),ct),(string)body["mime"]!);
        });
        group.MapPut("/media/{id:guid}",async(Guid id,int revision,Media input,Store db,CancellationToken ct)=>{
            input.Validate();var row=await db.Get("media",id,ct);var body=row["body"]!.AsObject();
            Rules.Need(body["deleting"]==null && body["pending"]==null,"Upload or deletion is pending.");
            foreach(var field in Rules.Body(input))body[field.Key]=field.Value?.DeepClone();
            return await db.Update("media",id,revision,body,null,ct);
        });
        group.MapDelete("/media/{id:guid}",async(Guid id,int revision,Store db,CancellationToken ct)=>{
            var row=await db.Get("media",id,ct);var body=row["body"]!.AsObject();
            if(body["pending"]!=null && DateTimeOffset.Parse((string)row["created_at"]!)>DateTimeOffset.UtcNow.AddMinutes(-3))
                throw new WorkspaceError(409,"Upload may still be running. Retry removal in three minutes.");
            body["deleting"]=true;
            await db.Update("media",id,revision,body,null,ct);
            await db.Remove(Guid.Parse((string)body["key"]!),ct);
            await db.Delete("media",id,revision+1,ct);
            return Results.Ok(new {deleted=true});
        });
        group.MapPost("/songs",(Song input,Store db,CancellationToken ct)=>{
            input.Validate();return db.Insert("songs",Rules.Body(input),ct);
        });
        group.MapPut("/songs/{id:guid}",(Guid id,int revision,Song input,Store db,CancellationToken ct)=>{
            input.Validate();return db.Update("songs",id,revision,Rules.Body(input),null,ct);
        });
        group.MapDelete("/songs/{id:guid}",async(Guid id,int revision,Store db,CancellationToken ct)=>{
            await db.Delete("songs",id,revision,ct);return Results.Ok(new {deleted=true});
        });
        group.MapPost("/services",(Plan input,Store db,CancellationToken ct)=>{
            input.Validate();return db.Insert("services",Rules.Body(new Service(input,new("Draft",-1,null,0,null))),ct);
        });
        group.MapPut("/services/{id:guid}",async(Guid id,int revision,Plan input,Store db,CancellationToken ct)=>{
            input.Validate();var row=await db.Get("services",id,ct);
            Rules.Need(row["owner_hash"]==null,"Stop this service before editing.");
            return await db.Update("services",id,revision,Rules.Body(new Service(input,new("Draft",-1,null,0,null))),null,ct);
        });
        group.MapGet("/services/{id:guid}/ownership",async(Guid id,HttpRequest req,Store db,CancellationToken ct)=>{
            var row=await db.Get("services",id,ct);
            return new {owned=(string?)row["owner_hash"]==Controller(req),revision=(int)row["revision"]!};
        });
        group.MapPost("/services/{id:guid}/command",async(Guid id,Command command,HttpRequest req,Store db,CancellationToken ct)=>{
            var row=await db.Get("services",id,ct);
            if((int)row["revision"]! != command.Revision)throw new WorkspaceError(409,"Service changed. Refresh the schedule.");
            var service=Rules.Read<Service>(row["body"]!);
            var owner=(string?)row["owner_hash"];
            if(owner!=null && owner!=Controller(req) && command.Action is not ("release" or "recover"))throw new WorkspaceError(409,"A different desk owns this service.");
                        var action=command.Action switch {
                "release"=>command with {Action="stop"},
                "recover" when owner!=null=>command with {Action="pause"},
                _=>command
            };
            var run=Runner.Apply(service,action,DateTimeOffset.UtcNow);
            if(action.Action=="arm" || command.Action=="recover")owner=Controller(req);
            if(run.Status=="Completed")owner=null;
            return await db.Update("services",id,command.Revision,Rules.Body(service with {Run=run}),owner,ct);
        });
        group.MapPost("/assist",Assistant.Prepare).RequireRateLimiting("model");
    }
    public static string Mime(byte[] bytes){
        var b=bytes.AsSpan();
        if(b.Length>=16){
            if(b.StartsWith("RIFF"u8)&&b.Slice(8,4).SequenceEqual("WEBP"u8))return "image/webp";
            if(b.StartsWith(new byte[]{137,80,78,71,13,10,26,10}))return "image/png";
            if(b.StartsWith(new byte[]{255,216,255}))return "image/jpeg";
            if(b.StartsWith("GIF89a"u8)||b.StartsWith("GIF87a"u8))return "image/gif";
            if(b.Slice(4,4).SequenceEqual("ftyp"u8) && b.Slice(8,4) is var brand && (brand.StartsWith("iso"u8)||brand.StartsWith("mp4"u8)||brand.SequenceEqual("avc1"u8)))return "video/mp4";
            if(b.StartsWith(new byte[]{26,69,223,163})&&b[..Math.Min(4096,b.Length)].IndexOf("webm"u8)>=0)return "video/webm";
        }
        throw new WorkspaceError(415,"Choose a WebP, PNG, JPEG, GIF, MP4 or WebM file.");
    }
    static async Task<JsonNode> Upload(HttpRequest request,Store db,CancellationToken ct){
        var limit=request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if(limit!=null&&!limit.IsReadOnly)limit.MaxRequestBodySize=MaxFile+65536;
        Rules.Need(request.HasFormContentType,"Upload a media file.");
        var form=await request.ReadFormAsync(ct);
        var file=form.Files.GetFile("file");
        Rules.Need(file is {Length:>0 and <=MaxFile},"Media must be between 1 byte and 32 MiB.");
        var media=new Media(form["title"].ToString(),form["category"].ToString());media.Validate();
        Rules.Need(form["prompt"].ToString().Length<=1500,"Prompt too long.");
        using var stream=new MemoryStream();await file!.CopyToAsync(stream,ct);
        var bytes=stream.ToArray();var mime=Mime(bytes);
        Rules.Need(int.TryParse(form["width"],out var width)&&width is >0 and <=32768,"Invalid width.");
        Rules.Need(int.TryParse(form["height"],out var height)&&height is >0 and <=32768,"Invalid height.");
                var key=Guid.NewGuid();var body=Rules.Body(media);
        var thumb=form["thumb"].ToString();
        if(thumb.Length>0){
            const string prefix="data:image/jpeg;base64,";
            Rules.Need(thumb.Length<=32768 && thumb.StartsWith(prefix,StringComparison.Ordinal),"Invalid thumbnail.");
            try { Rules.Need(Mime(Convert.FromBase64String(thumb[prefix.Length..]))=="image/jpeg","Invalid thumbnail format."); }
            catch(FormatException){throw new WorkspaceError(400,"Invalid thumbnail encoding.");}
            body["thumb"]=thumb;
        }
        body["prompt"]=form["prompt"].ToString();body["width"]=width;body["height"]=height;
        body["mime"]=mime;body["key"]=key.ToString();
        // Reserve the metadata first. Failed transfers remain identifiable and removable.
        body["pending"]=true;
        var reserved=await db.Insert("media",body,ct);
        var id=Guid.Parse((string)reserved["id"]!);
        await db.Upload(key,bytes,mime,ct);
        body.Remove("pending");
        return await db.Update("media",id,(int)reserved["revision"]!,body,null,ct);


    }
}
