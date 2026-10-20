import com.projectlibre1.pm.task.*;
import com.projectlibre1.pm.resource.*;
import com.projectlibre1.pm.assignment.*;
import com.projectlibre1.pm.dependency.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;

public class VerifyLevelSuite{
 static byte[]hash(Path p)throws Exception{return MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(p));}
 public static void main(String[]args)throws Exception{
  InspectLibra.init();Path dir=Path.of(args[0]);Path baseFile=dir.resolve("Проект_с_перегрузками.pod");Project base=InspectLibra.load(baseFile.toString());LevelLibra.recalc(base);double work=LevelLibra.totalWork(base);var baselineTasks=LevelLibra.tasks(base);
  BuildLibra.check(LevelLibra.conflicts(base).stream().map(LevelLibra.Conflict::resource).distinct().count()==4,"Expected four overloaded resources");
  String[]names={"Автоматическое_выравнивание","Замена_ресурсов","Ручное_выравнивание","Перенос_сверхурочные","Комбинированное_выравнивание"};
  for(String name:names){BuildLibra.check(Arrays.equals(hash(baseFile),hash(dir.resolve("Копии_до_выравнивания").resolve(name+".pod"))),"Initial copy differs");Project p=InspectLibra.load(dir.resolve(name+".pod").toString());LevelLibra.recalc(p);var ts=LevelLibra.tasks(p);
   BuildLibra.check(ts.size()==39,"Task count mismatch");
   for(var e:baselineTasks.entrySet()){NormalTask t=ts.get(e.getKey());BuildLibra.check(t.getName().equals(e.getValue().getName()),"Task name/order changed");BuildLibra.check(t.getPredecessors().equals(e.getValue().getPredecessors()),"Dependency changed on "+e.getKey()+" in "+name);BuildLibra.check(t.isWbsParent()==e.getValue().isWbsParent(),"Task hierarchy changed");}
   BuildLibra.check(ts.get(16).getConstraintType()==1,"ALAP missing");Dependency dep=(Dependency)ts.get(6).getPredecessorList().get(0);BuildLibra.check(dep.getLag()==2*LevelLibra.DAY,"Two-day lag missing");
   BuildLibra.check(LevelLibra.ass(p,14,BuildLibra.BA).getWorkContourType()==6&&LevelLibra.ass(p,19,BuildLibra.BA).getWorkContourType()==2,"Profiles missing");
   BuildLibra.check(Math.abs(LevelLibra.totalWork(p)-work)<.001,"Work changed");BuildLibra.check(LevelLibra.conflicts(p).isEmpty(),"Regular-time overload remains");
   for(Object x:p.getResourcePool().getResourceList()){Resource r=(Resource)x;if(r.isLabor())BuildLibra.check(((ResourceImpl)r).getMaximumUnits()==1,"Capacity artificially increased");}
   for(var e:ts.entrySet()){NormalTask t=e.getValue();for(Object x:t.getPredecessorList()){Dependency d=(Dependency)x;NormalTask predecessor=(NormalTask)d.getPredecessor();long earliest=p.getWorkCalendar().add(predecessor.getEnd(),d.getLag(),false);BuildLibra.check(t.getStart()>=earliest,"Predecessor/lag date violation: "+t.getId());}}
   double overtime=LevelLibra.totalOvertime(p);BuildLibra.check(name.equals("Перенос_сверхурочные")?overtime>80:name.equals("Комбинированное_выравнивание")?overtime>6:overtime==0,"Unexpected overtime");
   System.out.println("PASS "+name+": 39 tasks, original dependencies, two-day lag, ALAP, profiles, work preserved, max units 100%, no regular overload, overtime="+overtime);
  }System.exit(0);
 }
}
