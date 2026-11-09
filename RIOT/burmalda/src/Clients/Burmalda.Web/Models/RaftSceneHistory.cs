using Burmalda.Diagnostics;

namespace Burmalda.Web.Models;

public sealed record RaftSceneMoment(TraceEntry Event, TraceNode[] Nodes);
public sealed class RaftSceneHistory
{
    public List<RaftSceneMoment> Items { get; } = [];
    public Dictionary<int,(Guid Run,long Sequence)> Cursors { get; } = [];
    public bool IsLive { get; private set; } = true;
    public int Position { get; private set; }
    public int NewEvents { get; private set; }
    public string? Notice { get; private set; }
    private TraceNode[] current=[];
    public RaftSceneMoment? Selected => Items.Count==0?null:Items[Math.Clamp(Position,0,Items.Count-1)];
    public string Query => string.Join(';',Cursors.OrderBy(x=>x.Key).Select(x=>$"{x.Key},{x.Value.Run},{x.Value.Sequence}"));
    public void Select(int position) { IsLive=false; Position=Math.Clamp(position,0,Math.Max(0,Items.Count-1)); NewEvents=0; }
    public void Live() { IsLive=true; Position=Math.Max(0,Items.Count-1); NewEvents=0; }
    public void Ingest(VisualizationFrame frame)
    {
        if(current.Length==0) current=frame.Nodes;
        var incoming=new List<TraceEntry>();
        foreach(var node in frame.Nodes)
        {
            if(node.Trace is not {} page) continue;
            if(page.Gap)
            {
                Notice=$"Node {node.NodeId}: перезапуск или пропуск истории; неизвестные события не восстановлены.";
                var offset=Array.FindIndex(current,n=>n.NodeId==node.NodeId);
                if(offset>=0) { current=current.ToArray();current[offset]=node with {Checkpoint=null}; }
            }
            var previous=Cursors.GetValueOrDefault(node.NodeId);
            incoming.AddRange(page.Entries.Where(e=>e.RunId!=previous.Run || e.Sequence>previous.Sequence));
            Cursors[node.NodeId]=(page.RunId,page.Cursor);
        }
        // Arrival batches are ordered for display only. RPC IDs establish causality, not wall clocks.
        foreach(var e in incoming.OrderBy(e=>e.AtUtc).ThenBy(e=>e.NodeId).ThenBy(e=>e.Sequence))
        {
            if(e.State is {} state)
            {
                var offset=Array.FindIndex(current,n=>n.NodeId==state.NodeId);
                if(offset>=0)
                {
                    current=current.ToArray();var n=current[offset];
                    var old=n.Checkpoint;
                    current[offset]=n with {Available=true,Checkpoint=new(state,old?.Blocked??[],
                        old?.AuthorityConfirmed==true && old.State.Term==state.Term && state.Role=="Leader")};
                }
            }
            Items.Add(new(e,current));
        }
        current=frame.Nodes;
        var observation=new TraceEntry(Guid.Empty,0,frame.AtUtc,0,"Observation","Контрольное наблюдение пяти узлов");
        Items.Add(new(observation,current));
        if(!IsLive) NewEvents+=incoming.Count;
        var cutoff=frame.AtUtc-TimeSpan.FromMinutes(5);
        var remove=Math.Max(Items.Count-20000,Items.FindIndex(x=>x.Event.AtUtc>=cutoff));
        if(remove>0)
        {
            Items.RemoveRange(0,remove);
            if(!IsLive && Position<remove) Notice="Выбранный фрагмент вышел за пределы истории. Показан первый доступный.";
            Position=Math.Max(0,Position-remove);
        }
        if(IsLive) Position=Items.Count-1;
    }
    public object Export()=>new {isLive=IsLive,position=Position,notice=Notice,history=Items.Select(m=>new {entry=m.Event,nodes=m.Event.Kind=="Observation"?m.Nodes:null})};
}
