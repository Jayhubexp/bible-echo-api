using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BibleEcho.Workspace;
using BibleEcho.AI;
using BibleEcho.Data;
using Microsoft.Extensions.Configuration;

int passed=0;
const string sermon="We are going to turn our Bibles to look chapter 18. from verse 4 to verse 7.";
var luke=SpokenCitationLexer.Parse(sermon,speechAliases:true).Single();
Check("Misheard Luke resolves to the complete range",luke.FormattedReference=="Luke 18:4-7");
var bible=new SqliteBibleRepository(Path.Combine(AppContext.BaseDirectory,"data","bible.db"));
var passage=await bible.GetVersesAsync(luke.Book,luke.Chapter,luke.VerseStart,luke.VerseEnd);
Check("Projection starts at Luke 18:4 and includes verses 5–7",passage.Select(v=>v.Verse).SequenceEqual(new[]{4,5,6,7}));
Check("Ordinary speech does not infer Luke",SpokenCitationLexer.Parse("Look at chapter 18, verse 4. Look chapter 18 is interesting.",true).Count==0);
Check("Bookless repetition does not invent a new citation",SpokenCitationLexer.Parse("Chapter 18 From verse 4 to 7",true).Count==0);
Check("Manual lookup does not alias look",SpokenCitationLexer.Parse(sermon).Count==0);
Check("Canonical spoken range survives punctuation",SpokenCitationLexer.Parse("John chapter three, verses sixteen through verse eighteen").Single().FormattedReference=="John 3:16-18");
void Check(string name,bool success){if(!success)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
bool Invalid(Action fn){try{fn();return false;}catch(WorkspaceError){return true;}}
var cue=new Cue("Welcome","Announcements",1,"Welcome","",null,[],"center","Display","");
var plan=new Plan("Sunday","2026-11-08T09:30","Africa/Accra","",[cue,cue with {Title="Reading"}],"");
plan.Validate();var start=plan.Start();
Check("Accra local time resolves to UTC",start==DateTimeOffset.Parse("2026-11-08T09:30:00Z"));
Check("DST ambiguity rejected",Invalid(()=>(plan with{LocalStart="2026-11-01T01:30",Timezone="America/New_York"}).Start()));
var run=new Run("Draft",-1,null,0,null);
Run Change(string action,int seconds,int value=0)=>Runner.Apply(new(plan,run),new(action,1,value),start.AddSeconds(seconds));
run=Change("approve",-70);Check("Approval precedes arming",run.Status=="Scheduled");
Check("Missed start cannot arm",Invalid(()=>Change("arm",1)));
run=Change("arm",-60);Check("Armed countdown",run.Due==start);
Check("Early tick retains armed run",Change("tick",-1)==run);
Check("Late wake pauses without projection",Change("tick",20).Status=="Paused" && Change("tick",20).Index==-1);
Check("Short polling delay preserves the scheduled deadline",Change("tick",4).Due==start.AddMinutes(1));
run=Change("tick",0);Check("Scheduled first cue",run is {Index:0,Status:"Live"} && run.Due==start.AddMinutes(1));
run=Change("pause",20);Check("Pause preserves time",run.Remaining==40&&run.Due==null);
Check("Paused run rejects automatic tick",Invalid(()=>Change("tick",200)));
run=Change("extend",30,60);Check("Extension stays paused",run.Remaining==100&&run.Status=="Paused");
run=Change("resume",200);Check("Resume uses frozen remainder",run.Due==start.AddSeconds(300));
run=Change("tick",300);Check("Timed advance",run.Index==1);
run=Change("previous",305);Check("Manual navigation pauses",run.Status=="Paused"&&run.Index==0);
run=Change("jump",306,1);Check("Jump to approved item",run.Index==1&&run.Status=="Paused");
run=Change("stop",310);Check("Stop releases timeline",run.Status=="Completed"&&run.Due==null);
Check("Reject active HTML with video extension",Invalid(()=>Endpoints.Mime("<html>unsafe data</html>"u8.ToArray())));
Check("Recognize WebP bytes",Endpoints.Mime("RIFF0000WEBPVP8 bytes"u8.ToArray())=="image/webp");
var song=new Song("Song","Choir","Hymn","English","grace","Choir", [new("Verse","Quiet morning"),new("Chorus","We sing")],[0,1,0],null,true,false);
song.Validate();Check("Song arrangement persists through JSON",Rules.Read<Song>(Rules.Body(song)).Arrangement.SequenceEqual(new[]{0,1,0}));
Check("Invalid slide references rejected",Invalid(()=>(song with{Arrangement=[2]}).Validate()));
// Exercise real request serialization, model response parsing and lyric guard with a stub transport.
var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
    ["SUPABASE_URL"]="https://example.invalid",["SUPABASE_SERVICE_ROLE_KEY"]="test-key",["MODEL_API_KEY"]="test-key"
}).Build();
string draft="";
var http=new HttpClient(new Handler(async req=>{
    if(req.RequestUri!.AbsolutePath.StartsWith("/rest/"))return Json("[]");
    var payload=JsonNode.Parse(await req.Content!.ReadAsStringAsync())!;
    if((string?)payload["model"]!="muse-spark-1.3")throw new Exception("Unexpected model.");
    return Json(JsonSerializer.Serialize(new {choices=new[]{new {message=new {content=draft}}}}));
})){BaseAddress=new Uri("https://example.invalid/")};
var db=new Store(http,config);var meta=new MetaClient(http,config,new Factory(http));
draft="""{"sections":[{"label":"Verse","text":"Quiet morning."}],"arrangement":[0],"background":null,"lines":2,"size":4}""";
var formatted=await Assistant.Prepare(new("format","quiet morning",true),meta,db,default);
Check("Validated AI formatting draft",(string?)formatted["sections"]![0]!["text"]=="Quiet morning.");
try{await Assistant.Prepare(new("format","other lyrics",true),meta,db,default);Check("Reject invented lyrics",false);}catch(WorkspaceError e){Check("Reject invented lyrics",e.Status==422);}
draft=JsonSerializer.Serialize(new {summary="One minute welcome.",items=new[]{cue}},Rules.Json);
Check("AI schedule validated",(await Assistant.Prepare(new("plan","Welcome"),meta,db,default))["items"]!.AsArray().Count==1);
config["SUPABASE_SERVICE_ROLE_KEY"]="sb_secret_fixture";
using var secretHttp=new HttpClient(new Handler(req=>{
    Check("Opaque Supabase secret uses only the API key header",req.Headers.Authorization==null && req.Headers.GetValues("apikey").Single()=="sb_secret_fixture");
    return Task.FromResult(Json("[]"));
}));
await new Store(secretHttp,config).List("media",default);
Console.WriteLine($"{passed} checks passed.");
static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,System.Text.Encoding.UTF8,"application/json")};
sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request);
}
sealed class Factory(HttpClient http):IHttpClientFactory{public HttpClient CreateClient(string name)=>http;}
