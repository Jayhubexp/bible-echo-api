using System.Text.Json;
using System.Text.Json.Nodes;

namespace BibleEcho.Workspace;

public static class Rules {
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static void Need(bool valid,string message) {
        if(!valid)throw new WorkspaceError(400,message);
    }
    public static void Title(string? title)=>Need(!string.IsNullOrWhiteSpace(title)&&title.Length<=180,"Enter a title of up to 180 characters.");
    public static JsonObject Body<T>(T value)=>JsonSerializer.SerializeToNode(value,Json)!.AsObject();
    public static T Read<T>(JsonNode node)=>node.Deserialize<T>(Json)??throw new WorkspaceError(400,"Invalid document.");
    public static readonly string[] Categories=["Worship","Praise","Hymns","Sermon","Prayer","Thanksgiving","Offering","Announcements","Custom"];
}
public record Media(string Title,string Category,string Tags="",bool Favorite=false,string Motion="Still",int Duration=12,bool Loop=true,double Speed=1) {
    public void Validate() {
        Rules.Title(Title);
        Rules.Need(Rules.Categories.Contains(Category)&&Tags?.Length<=500,"Invalid category or tags.");
        Rules.Need(Motion is "Still" or "Zoom" or "Pan" or "Drift" && Duration is 8 or 12 or 15 && Speed is >=0.25 and <=2,"Invalid motion configuration.");
    }
}
public record Section(string Label,string Text);
public record Song(string Title,string Artist,string Category,string Language,string Theme,string License,Section[] Sections,int[] Arrangement,
    Guid? Background,bool Favorite,bool Archived,int Lines=3,double Size=5,string Font="Georgia",string Align="center",string Color="#ffffff",int Margin=5,Dictionary<string,int[]>? Alternatives=null) {
    public void Validate(){
        Rules.Title(Title);
        Rules.Need(Category is "Hymn" or "Worship" or "Praise" or "Gospel" or "Special Ministration" or "Other","Invalid song category.");
        Rules.Need(Sections is {Length:>0 and <=100} && Sections.All(s=>s!=null&&!string.IsNullOrWhiteSpace(s.Label)&&s.Label.Length<=80&&!string.IsNullOrWhiteSpace(s.Text)&&s.Text.Length<=8000),"Each section needs a label and lyrics.");
        Rules.Need(Arrangement is {Length:>0 and <=200} && Arrangement.All(i=>i>=0 && i<Sections!.Length),"Arrangement contains an unknown section.");
        Rules.Need(Alternatives==null || (Alternatives.Count<=20 && Alternatives.All(a=>a.Key.Length is >0 and <=80 && a.Value is {Length:>0 and <=200} && a.Value.All(i=>i>=0 && i<Sections!.Length))),"Invalid alternate arrangement.");
        Rules.Need(Lines is >=1 and <=8 && Size is >=2 and <=10 && Margin is >=0 and <=15 && Font is "Georgia" or "Arial" or "Verdana" && Align is "left" or "center" or "right" && System.Text.RegularExpressions.Regex.IsMatch(Color??"","^#[0-9a-fA-F]{6}$"),"Invalid slide style.");
    }
}
public record Cue(string Title,string Type,int Minutes,string Text,string Reference,Guid? Background,Guid[] Songs,string Layout,string Action,string Notes) {
    public static readonly string[] Types=["Opening prayer","Praise and worship","Hymns","Congregational songs","Bible reading","Sermon","Offering","Announcements","Special ministration","Closing prayer","Custom item"];
    public void Validate(){
        Rules.Title(Title);
        Rules.Need(Text is {Length:<=32000} && Reference is {Length:<=300} && Notes is {Length:<=4000},"Invalid display text, reference or notes.");
        Rules.Need(Types.Contains(Type)&&Minutes is >=1 and <=360 && Songs is {Length:<=40} && Layout is "center" or "left" && Action is "Display" or "Clear" or "Hold","Invalid service item.");
    }
}
public record Plan(string Title,string LocalStart,string Timezone,string Location,Cue[] Items,string Summary) {
    public DateTimeOffset Start(){
        Rules.Need(DateTime.TryParseExact(LocalStart,"yyyy-MM-ddTHH:mm",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var local),"Enter a valid service date and time.");
        if(!TimeZoneInfo.TryFindSystemTimeZoneById(Timezone??"",out var zone))throw new WorkspaceError(400,"Unknown timezone.");
        Rules.Need(!zone.IsInvalidTime(local)&&!zone.IsAmbiguousTime(local),"Choose an unambiguous local start time.");
        return TimeZoneInfo.ConvertTimeToUtc(local,zone);
    }
    public void Validate(){
        Rules.Title(Title);Start();
        Rules.Need(Items is {Length:>0 and <=150},"Provide between 1 and 150 service items.");
        foreach(var item in Items!) {Rules.Need(item!=null,"Empty item.");item!.Validate();}
    }
}
public record Run(string Status,int Index,DateTimeOffset? Due,int Remaining,DateTimeOffset? ActualStart);
public record Service(Plan Plan,Run Run) {
    public DateTimeOffset StartUtc=>Plan.Start();
    public DateTimeOffset EndUtc=>StartUtc.AddMinutes(Plan.Items.Sum(i=>i.Minutes));
}
public record Command(string Action,int Revision,int Value=0);
public static class Runner {
    public static Run Apply(Service service,Command command,DateTimeOffset now) {
        var run=service.Run;var plan=service.Plan;var start=plan.Start();var items=plan.Items;
        var remaining=run.Due is {} due ? Math.Max(0,(int)Math.Ceiling((due-now).TotalSeconds)):run.Remaining;
        Run At(int index,bool live,DateTimeOffset? scheduled=null) {
            Rules.Need(index>=0&&index<items.Length,"Choose an existing item.");
            var seconds=items[index].Minutes*60;
            return new(live?"Live":"Paused",index,live?(scheduled??now).AddSeconds(seconds):null,seconds,now);
        }
        switch(command.Action){
            case "approve" when run.Status=="Draft": return run with {Status="Scheduled"};
            case "arm" when run.Status=="Scheduled":
                Rules.Need(now<=start,"Start time has passed. Edit and approve a new start time.");
                return run with {Status="Armed",Due=start};
            case "tick" when run.Status is "Armed" or "Live":
                if(now<run.Due)return run;
                if(now-run.Due>TimeSpan.FromSeconds(10))return run with {Status="Paused",Due=null,Remaining=remaining};
                if(run.Index+1==items.Length)return run with {Status="Completed",Due=null};
                return At(run.Index+1,true,run.Due);
        }
        Rules.Need(run.Status is "Live" or "Paused" or "Armed","This command requires an armed service.");
        return command.Action switch {
            "pause"=>run with {Status="Paused",Due=null,Remaining=remaining},
            "resume" when run.Status=="Paused" && run.Index<0 && now<start=>run with {Status="Armed",Due=start},
            "resume" when run.Status=="Paused" && run.Index<0=>At(0,true),
            "resume" when run.Status=="Paused"=>run with {Status="Live",Due=now.AddSeconds(Math.Max(1,remaining))},
            "next" or "skip" when run.Index+1>=items.Length=>run with {Status="Completed",Due=null},
            "next" or "skip"=>At(run.Index+1,false),
            "previous"=>At(Math.Max(0,run.Index-1),false),
            "jump"=>At(command.Value,false),
            "extend" when command.Value is >0 and <=3600 && run.Index>=0=>run with {Status="Paused",Due=null,Remaining=remaining+command.Value},
            "stop"=>run with {Status="Completed",Due=null},
            _=>throw new WorkspaceError(400,"Command unavailable in this state.")
        };
    }
}
