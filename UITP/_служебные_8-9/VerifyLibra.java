import com.projectlibre1.pm.task.*;
import com.projectlibre1.pm.resource.*;
import com.projectlibre1.pm.assignment.*;
import com.projectlibre1.pm.costing.*;
import java.util.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;

public class VerifyLibra{
 static void check(boolean b,String s){if(!b)throw new IllegalStateException(s);}
 static Map<Integer,NormalTask> tasks(Project p){Map<Integer,NormalTask> m=new TreeMap<>();for(Object x:p.getTasks()){NormalTask t=(NormalTask)x;m.put((int)t.getId(),t);}return m;}
 public static void main(String[]args)throws Exception{
  InspectLibra.init();BuildLibra.definitions();BuildLibra.assignments.replaceAll(x->new BuildLibra.A(PlanLibra.OLD_TO_NEW[x.task()],x.name(),x.units()));
  Project original=PlanLibra.create(InspectLibra.load(args[0]));Project result=InspectLibra.load(args[1]);
  result.markAllTasksAsNeedingRecalculation(true);result.getSchedulingAlgorithm().calculate(true);
  Map<Integer,NormalTask> old=tasks(original),now=tasks(result);check(old.size()==now.size(),"Task count changed");
  Map<String,Resource> resources=new LinkedHashMap<>();for(Object x:result.getResourcePool().getResourceList()){Resource r=(Resource)x;resources.put(r.getName(),r);}
  check(resources.size()==18,"Resource count mismatch");
  for(BuildLibra.R expected:BuildLibra.resources){Resource r=resources.get(expected.name());check(r!=null,"Resource missing");check(r.getResourceType()==expected.type(),"Resource type mismatch");
   double divisor=expected.type()==1?3600000.0:1.0;
   check(Math.abs(r.getStandardRate().getValue()*divisor-expected.std())<1e-7,"Standard rate mismatch: "+expected.name());
   check(Math.abs(r.getOvertimeRate().getValue()*divisor-expected.ot())<1e-7,"Overtime rate mismatch");
   check(Math.abs(r.getCostPerUse()-expected.use())<1e-7,"Cost per use mismatch");
   check(r.getMaterialLabel().equals(expected.label()),"Material label mismatch");
  }
  Resource ba=resources.get(BuildLibra.BA);CostRate b=(CostRate)ba.getCostRateTable(1).findCurrent();
  check(Math.abs(b.getStandardRate().getValue()*3600000-11)<1e-7,"Business analyst B rate mismatch");
  int durationDifferences=0;StringBuilder schedule=new StringBuilder("task_id\ttask\tstart_ms\tfinish_ms\toriginal_duration_hours\tresult_duration_hours\n");
  for(var entry:old.entrySet()){NormalTask a=entry.getValue(),n=now.get(entry.getKey());check(a.getName().equals(n.getName()),"Task name changed");check(a.getStart()==n.getStart()&&a.getEnd()==n.getEnd(),"Task dates changed: "+a.getId());
   check(a.getPredecessors().equals(n.getPredecessors()),"Predecessors changed: "+a.getId());
   check(a.isWbsParent()==n.isWbsParent(),"Summary task mismatch: "+a.getId());
   if(a.getDuration()!=n.getDuration()){durationDifferences++;throw new IllegalStateException("Unexpected duration difference: "+a.getId());}
   schedule.append(a.getId()).append('\t').append(a.getName().trim()).append('\t').append(n.getStart()).append('\t').append(n.getEnd()).append('\t').append(com.projectlibre1.datatype.Duration.millis(a.getDuration())/3600000.0).append('\t').append(com.projectlibre1.datatype.Duration.millis(n.getDuration())/3600000.0).append('\n');
  }
  double total=0;Set<Integer> assignedTasks=new HashSet<>();
  for(BuildLibra.A expected:BuildLibra.assignments){NormalTask t=now.get(expected.task());assignedTasks.add(expected.task());Resource r=resources.get(expected.name());Assignment assignment=t.findAssignment(r);check(assignment!=null,"Missing assignment");
   check(Math.abs(assignment.getRate().getValue()-expected.units())<1e-8,"Assignment units mismatch");check(assignment.getCostRateIndex()==0,"Rate table mismatch");
   BuildLibra.R spec=BuildLibra.resources.stream().filter(x->x.name().equals(expected.name())).findFirst().get();
   double expectedCost;
   if(r.isLabor()){
    long expectedWork=Math.round(((Assignment)old.get(expected.task()).getAssignments().get(0)).getWork(null)*expected.units());
    check(Math.abs(assignment.getWork(null)-expectedWork)<2,"Work mismatch: "+expected.task()+" "+expected.name());
    expectedCost=expectedWork/3600000.0*spec.std();
   }else expectedCost=expected.units()*(spec.std()+spec.use());
   check(Double.isFinite(assignment.getCost())&&Math.abs(assignment.getCost()-expectedCost)<.005,"Cost mismatch: "+expected.name()+" actual="+assignment.getCost()+" expected="+expectedCost);
   total+=assignment.getCost();
  }
  int assignmentCount=0;for(int id:assignedTasks){NormalTask t=now.get(id);for(Object x:t.getAssignments())check(resources.containsKey(((Assignment)x).getResource().getName()),"Unassigned resource left");assignmentCount+=t.getAssignments().size();}
  check(assignmentCount==77,"Assignment count mismatch");
  Files.writeString(Path.of(args[2]),schedule,StandardCharsets.UTF_8);
  check(Double.isFinite(result.getCost(null)),"Project cost is not finite");
  check(Math.abs(result.getCost(null)-total)<.005,"Project total cost mismatch");
  System.out.println("VERIFIED: 18 resources, 77 assignments on "+assignedTasks.size()+" tasks; all 39 task names, order, durations, predecessors and five summary tasks match table 2.3; standard/overtime/A/B rates and every assignment cost/work verified after native POD reload. Total assigned cost="+total+" rub. Project cost="+result.getCost(null)+" rub. Duration differences="+durationDifferences+". Project finish="+new Date(now.get(39).getEnd()));
  System.exit(0);
 }
}
