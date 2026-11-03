using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BibleEcho.Workspace;
public record Preparation(string Mode,string Text,bool Authorized=false);
public static class Assistant {
    public static async Task<JsonObject> Prepare(Preparation input,MetaClient meta,Store db,CancellationToken ct){
        Rules.Need(!string.IsNullOrWhiteSpace(input.Text)&&input.Text.Length<=32000,"Provide up to 32,000 characters.");
        Rules.Need(input.Mode is "plan" or "format" or "original" or "recommend","Unknown assistant mode.");
        Rules.Need(input.Mode!="format" || input.Authorized,"Confirm permission to prepare these lyrics.");
        var media=await db.List("media",ct);
        var catalog=media.Where(r=>r!["body"]!["deleting"]==null && r["body"]!["pending"]==null).Take(200).Select(r=>new {id=(string?)r!["id"],title=(string?)r["body"]!["title"],category=(string?)r["body"]!["category"]}).ToArray();
        var songs=input.Mode is "plan" or "recommend"?(await db.List("songs",ct)).Where(r=>(bool?)r!["body"]!["archived"]!=true).Take(200).Select(r=>new {id=(string?)r!["id"],title=(string?)r["body"]!["title"],theme=(string?)r["body"]!["theme"]}).ToArray():[];
        var instructions=input.Mode=="recommend" ?
            "Suggest songs by theme only from the given catalog. Return JSON {summary:string,songs:[catalog UUIDs]}. Do not include lyrics. Return no matches if appropriate." :
            input.Mode=="plan" ?
            "Return JSON {summary:string,items:Cue[]}. Each Cue has title,type,minutes (integer 1-360),text (only supplied announcement or lyrics; otherwise empty),reference (only supplied scripture reference),background (UUID from catalog or null),songs (array of song catalog UUIDs),layout (center or left),action (Display, Clear or Hold),notes. Type must be one of "+string.Join(", ",Cue.Types)+". Suggest realistic durations, local start times, layouts and transitions. In the summary explain any overlaps in the supplied outline, proposed fixes and missing scripture, songs or media. Never supply missing song lyrics." :
            "Return JSON {sections:[{label,text}],arrangement:[zero-based section indices],background:catalog UUID or null,lines:integer 2-4,size:number 3-8}. "+
            (input.Mode=="format"?"Only segment and format supplied lyrics. Preserve every word in order, do not expand excerpts.":"Compose original worship lyrics for the requested theme. Never reproduce, complete, translate or imitate a named existing song. Refuse requests to retrieve lyrics.")+
            " Recommend background by theme. No lyrics database or web retrieval.";
        var result=await meta.Draft(instructions,new {outline=input.Text,media=catalog,songs},ct);
        bool Known(JsonNode? id)=>id==null || catalog.Any(x=>x.id==(string?)id);
        try{
            if(input.Mode=="recommend"){
                var ids=Rules.Read<Guid[]>(result["songs"]!);
                Rules.Need(ids.Length<=20 && ids.All(id=>songs.Any(s=>s.id==id.ToString())) && result["summary"]?.GetValue<string>() is {Length:>0 and <=4000},"Invalid recommendations.");
            }else if(input.Mode=="plan"){
                var items=Rules.Read<Cue[]>(result["items"]!);
                Rules.Need(items.Length is >0 and <=150 && result["summary"]?.GetValue<string>() is {Length:>0},"Invalid plan draft.");
                foreach(var item in items){
                    item.Validate();
                    Rules.Need(string.IsNullOrWhiteSpace(item.Text)||input.Text.Contains(item.Text,StringComparison.OrdinalIgnoreCase),"Use only supplied display text.");
                    Rules.Need(item.Background==null||catalog.Any(x=>x.id==item.Background.ToString()),"Unknown media suggestion.");
                    Rules.Need(item.Songs.All(id=>songs.Any(s=>s.id==id.ToString())),"Unknown song suggestion.");
                }
            }else{
                var sections=Rules.Read<Section[]>(result["sections"]!);
                var arrangement=Rules.Read<int[]>(result["arrangement"]!);
                var song=new Song("Draft","","Other","","","",sections,arrangement,null,false,false,(int)result["lines"]!,(double)result["size"]!);
                song.Validate();Rules.Need(Known(result["background"]),"Unknown background suggestion.");
                static string Words(string text)=>Regex.Replace(text.ToLowerInvariant(),@"[^\p{L}\p{N}]","");
                if(input.Mode=="format")Rules.Need(Words(input.Text)==Words(string.Join(" ",sections.Select(s=>s.Text))),"Draft changed the supplied words. Keep the original or retry without section headings.");
            }
        }catch(Exception e) when(e is not OperationCanceledException){
            throw new WorkspaceError(422,"The assistant returned an invalid draft. Your existing work is unchanged.");
        }
        return result;
    }
}
