import com.projectlibre1.pm.task.*;
import com.projectlibre1.pm.resource.*;
import com.projectlibre1.pm.assignment.*;
import com.projectlibre1.pm.assignment.contour.*;
import com.projectlibre1.pm.calendar.*;
import com.projectlibre1.pm.dependency.*;
import com.projectlibre1.pm.scheduling.ConstraintType;
import com.projectlibre1.exchange.LocalFileImporter;
import java.util.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;
import java.time.*;
import java.time.format.DateTimeFormatter;

public class LevelLibra {
 static final long H=3600000L,DAY=8*H;static final double EPS=1e-6;
 static final DateTimeFormatter F=DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm").withZone(ZoneId.of("Europe/Minsk"));
 record Segment(Resource r,Assignment a,long start,long end,double units){}
 record Conflict(String resource,long start,long end,double load,double excessHours,String tasks){}
 static Map<Integer,NormalTask>tasks(Project p){return VerifyLibra.tasks(p);}
 static Resource resource(Project p,String name){for(Object x:p.getResourcePool().getResourceList()){Resource r=(Resource)x;if(r.getName().equals(name))return r;}throw new IllegalArgumentException(name);}
 static Assignment ass(Project p,int id,String name){Assignment a=tasks(p).get(id).findAssignment(resource(p,name));BuildLibra.check(a!=null,"Assignment missing: "+id+" "+name);return a;}
 static long overtime(Assignment a)throws Exception{var f=Assignment.class.getDeclaredField("detail");f.setAccessible(true);Object detail=f.get(a);var m=detail.getClass().getMethod("getOvertimeWork");return (long)m.invoke(detail);}
 static void recalc(Project p){p.markAllTasksAsNeedingRecalculation(true);p.getSchedulingAlgorithm().calculate(true);}
 static void profile(Assignment a,int type){long work=a.getWork(null);a.setWorkContourType(type);double now=a.getWork(null);a.getRate().setValue(a.getRate().getValue()*work/now);BuildLibra.check(Math.abs(a.getWork(null)-work)<2000,"Profile changed work");}
 static List<Segment>segments(Project p){List<Segment>list=new ArrayList<>();for(Object tx:p.getTasks()){NormalTask t=(NormalTask)tx;if(t.isWbsParent())continue;for(Object ax:t.getAssignments()){Assignment a=(Assignment)ax;Resource r=a.getResource();if(!r.isLabor()||a.getDurationMillis()==0)continue;long offset=0;WorkCalendar cal=a.getEffectiveWorkCalendar();for(AbstractContourBucket b:a.getWorkContour().getContourBuckets()){long duration=b.getBucketDuration(a.getDurationMillis());long start=cal.add(a.getStart(),offset,false);long end=cal.add(a.getStart(),offset+duration,true);double units=b.getEffectiveUnits(a.getUnits());if(duration>0&&units>EPS)list.add(new Segment(r,a,start,end,units));offset+=duration;}}}return list;}
 static List<Conflict>conflicts(Project p){List<Conflict>out=new ArrayList<>();Map<String,List<Segment>>groups=new TreeMap<>();for(Segment s:segments(p))groups.computeIfAbsent(s.r().getName(),k->new ArrayList<>()).add(s);
  for(var entry:groups.entrySet()){List<Segment>ss=entry.getValue();TreeSet<Long>bounds=new TreeSet<>();for(Segment s:ss){bounds.add(s.start());bounds.add(s.end());}List<Long>bb=new ArrayList<>(bounds);Resource r=ss.get(0).r();double cap=((ResourceImpl)r).getMaximumUnits();for(int i=0;i<bb.size()-1;i++){long begin=bb.get(i),end=bb.get(i+1);long duration=r.getWorkCalendar().compare(end,begin,false);if(duration<=0)continue;double load=0;Set<Integer>ids=new TreeSet<>();for(Segment s:ss)if(s.start()<end&&s.end()>begin){load+=s.units();ids.add((int)s.a().getTask().getId());}if(load>cap+EPS)out.add(new Conflict(entry.getKey(),begin,end,load,(load-cap)*duration/H,ids.toString()));}}
  return out;
 }
 static double totalWork(Project p){double sum=0;for(Object tx:p.getTasks()){NormalTask t=(NormalTask)tx;if(t.isWbsParent())continue;for(Object ax:t.getAssignments()){Assignment a=(Assignment)ax;if(a.getResource().isLabor())sum+=a.getWork(null)/(double)H;}}return sum;}
 static double totalOvertime(Project p)throws Exception{double sum=0;for(Object tx:p.getTasks()){NormalTask t=(NormalTask)tx;if(t.isWbsParent())continue;for(Object ax:t.getAssignments()){Assignment a=(Assignment)ax;if(a.getResource().isLabor())sum+=overtime(a)/(double)H;}}return sum;}
 static void datesAfter(Project p,int task,int after){NormalTask t=tasks(p).get(task);long date=tasks(p).get(after).getEnd();t.setScheduleConstraint(ConstraintType.SNET,date);recalc(p);}
 static void extend(Project p,int task,long duration){NormalTask t=tasks(p).get(task);Map<Assignment,Long>work=new LinkedHashMap<>();for(Object ax:t.getAssignments()){Assignment a=(Assignment)ax;if(a.getResource().isLabor())work.put(a,a.getWork(null));a.adjustRemainingDuration(duration,false);}t.setRawDuration(duration);for(var entry:work.entrySet()){Assignment a=entry.getKey();double current=a.getWork(null);a.getRate().setValue(a.getRate().getValue()*entry.getValue()/current);}recalc(p);}
 static Resource cloneResource(Project p,String source,String name)throws Exception{Resource existing=resource(p,source);Resource r=p.getResourcePool().createScriptedResource();r.setName(name);r.setResourceType(1);((ResourceImpl)r).setMaximumUnits(1);r.setStandardRate((com.projectlibre1.datatype.Rate)existing.getStandardRate().clone());r.setOvertimeRate((com.projectlibre1.datatype.Rate)existing.getOvertimeRate().clone());r.setBaseCalendar(p.getWorkCalendar());r.setGroup("A");r.setNotes("Заменяющий специалист для задания по выравниванию. Те же квалификация и ставки, что у ресурса "+source+".");return r;}
 static void replace(Project p,int id,String source,Resource target){NormalTask t=tasks(p).get(id);Assignment old=ass(p,id,source);Assignment replacement=(Assignment)old.cloneWithResourceAndTask(target,t);replacement.setUniqueId(old.getUniqueId());int index=t.getAssignments().indexOf(old);t.getAssignments().set(index,replacement);old.getResource().removeAssignment(old);target.addAssignment(replacement);}
 static void moveOvertime(Project p,int id,String name,double regularFraction)throws Exception{Assignment a=ass(p,id,name);long total=a.getWork(null);a.getRate().setValue(a.getRate().getValue()*regularFraction);long regular=a.getWork(null);a.setOvertimeWork(total-regular);BuildLibra.check(Math.abs(a.getWork(null)-total)<2000,"Overtime changed work for "+id+" "+name);}
 static void save(Project p,Path file,String notes)throws Exception{p.setNotes(p.getNotes()+"\n"+notes);Files.deleteIfExists(file);LocalFileImporter out=new LocalFileImporter();out.setProject(p);out.setFileName(file.toString());out.exportFile();BuildLibra.check(Files.size(file)>1000,"POD save failed");}
 static void export(Project p,Path dir,String tag)throws Exception{
  StringBuilder s=new StringBuilder("resource\tstart\tfinish\tload_percent\texcess_hours\ttasks\n");for(Conflict c:conflicts(p))s.append(c.resource()).append('\t').append(F.format(Instant.ofEpochMilli(c.start()))).append('\t').append(F.format(Instant.ofEpochMilli(c.end()))).append('\t').append(c.load()*100).append('\t').append(c.excessHours()).append('\t').append(c.tasks()).append('\n');Files.writeString(dir.resolve(tag+" — перегрузки.tsv"),s,StandardCharsets.UTF_8);
  StringBuilder a=new StringBuilder("task_id\ttask\tresource\tunits_percent\tprofile\twork_hours\tovertime_hours\tcost\tstart\tfinish\n");
  for(Object tx:p.getTasks()){NormalTask t=(NormalTask)tx;if(t.isWbsParent())continue;for(Object ax:t.getAssignments()){Assignment x=(Assignment)ax;if(!x.getResource().isLabor())continue;a.append(t.getId()).append('\t').append(t.getName()).append('\t').append(x.getResource().getName()).append('\t').append(x.getUnits()*100).append('\t').append(x.getWorkContourType()).append('\t').append(x.getWork(null)/(double)H).append('\t').append(overtime(x)/(double)H).append('\t').append(x.getCost()).append('\t').append(F.format(Instant.ofEpochMilli(t.getStart()))).append('\t').append(F.format(Instant.ofEpochMilli(t.getEnd()))).append('\n');}}
  Files.writeString(dir.resolve(tag+" — назначения.tsv"),a,StandardCharsets.UTF_8);
  StringBuilder tsv=new StringBuilder("id\ttask\tsummary\tdays\tpredecessors\tconstraint\tstart\tfinish\n");for(var entry:tasks(p).entrySet()){NormalTask t=entry.getValue();tsv.append(t.getId()).append('\t').append(t.getName()).append('\t').append(t.isWbsParent()).append('\t').append(com.projectlibre1.datatype.Duration.millis(t.getDuration())/(double)DAY).append('\t').append(t.getPredecessors()).append('\t').append(t.getConstraintType()).append('\t').append(F.format(Instant.ofEpochMilli(t.getStart()))).append('\t').append(F.format(Instant.ofEpochMilli(t.getEnd()))).append('\n');}Files.writeString(dir.resolve(tag+" — план.tsv"),tsv,StandardCharsets.UTF_8);
 }
 static void automatic(Project p)throws Exception{
  // Capacity-driven serial scheduling of noncritical preparatory work and finish bounds for ALAP work.
  List<Conflict>before=conflicts(p);
  if(before.stream().anyMatch(c->c.resource().equals(BuildLibra.PM)))datesAfter(p,9,8);
  if(before.stream().anyMatch(c->c.resource().equals(BuildLibra.QA)))datesAfter(p,18,14);
  if(conflicts(p).stream().anyMatch(c->c.resource().equals(BuildLibra.BA)))datesAfter(p,37,36);
  if(conflicts(p).stream().anyMatch(c->c.resource().equals(BuildLibra.TL))){NormalTask alap=tasks(p).get(16),busy=tasks(p).get(35),finish=tasks(p).get(39);long needed=p.getWorkCalendar().add(busy.getEnd(),alap.getDuration(),true);if(needed>finish.getEnd()){finish.setScheduleConstraint(ConstraintType.SNET,needed);recalc(p);}}
  BuildLibra.check(conflicts(p).isEmpty(),"Automatic capacity scheduling did not resolve all conflicts: "+conflicts(p));
 }
 public static void main(String[]args)throws Exception{
  InspectLibra.init();Path dir=Path.of(args[1]);Files.createDirectories(dir);Path audits=dir.resolve("Проверки");Files.createDirectories(audits);Path copies=dir.resolve("Копии_до_выравнивания");Files.createDirectories(copies);
  Project base=InspectLibra.load(args[0]);Map<Integer,NormalTask>ts=tasks(base);
  Dependency lag=(Dependency)ts.get(6).getPredecessorList().get(0);BuildLibra.check(lag.getPredecessorIdNumber()==5,"Wrong lag link");lag.setLag(2*DAY);
  ts.get(16).setScheduleConstraint(ConstraintType.ALAP,0);recalc(base);
  profile(ass(base,14,BuildLibra.BA),ContourTypes.BELL);profile(ass(base,19,BuildLibra.BA),ContourTypes.FRONT_LOADED);recalc(base);
  Path baseline=dir.resolve("Проект_с_перегрузками.pod");save(base,baseline,"Задания 10–16. Связь 5→6: задержка 2 рабочих дня. Задача 16: как можно позже. Бизнес-аналитик: колоколообразный профиль на задаче 14, профиль загрузки в начале на задаче 19, остальные плоские. Полный объём работы сохранён, пиковые единицы адаптированы к профилям. Материальные затраты не выравниваются.");
  base=InspectLibra.load(baseline.toString());recalc(base);export(base,audits,"Исходный");System.out.println("BASE work="+totalWork(base)+" cost="+base.getCost(null)+" conflicts="+conflicts(base));
  String[]names={"Автоматическое_выравнивание","Замена_ресурсов","Ручное_выравнивание","Перенос_сверхурочные","Комбинированное_выравнивание"};
  StringBuilder summary=new StringBuilder("method\tfinish\tcost\twork_hours\tovertime_hours\tconflict_intervals\toverloaded_resources\n");
  summary.append("Исходный\t").append(F.format(Instant.ofEpochMilli(tasks(base).get(39).getEnd()))).append('\t').append(base.getCost(null)).append('\t').append(totalWork(base)).append('\t').append(totalOvertime(base)).append('\t').append(conflicts(base).size()).append('\t').append(conflicts(base).stream().map(Conflict::resource).distinct().count()).append('\n');
  for(int mode=0;mode<names.length;mode++){
   String name=names[mode];Files.copy(baseline,copies.resolve(name+".pod"),StandardCopyOption.REPLACE_EXISTING);Project p=InspectLibra.load(baseline.toString());
   switch(mode){
    case 0->automatic(p);
    case 1->{Resource qa=cloneResource(p,BuildLibra.QA,"Тест-инженер 2"),tl=cloneResource(p,BuildLibra.TL,"Технический лидер 2"),pm=cloneResource(p,BuildLibra.PM,"Руководитель проекта 2");replace(p,9,BuildLibra.PM,pm);replace(p,18,BuildLibra.QA,qa);replace(p,15,BuildLibra.TL,tl);replace(p,16,BuildLibra.TL,tl);replace(p,37,BuildLibra.BA,resource(p,BuildLibra.SA));recalc(p);}
    case 2->{extend(p,8,2*DAY);extend(p,9,4*DAY);datesAfter(p,18,14);extend(p,36,10*DAY);extend(p,37,4*DAY);}
    case 3->{extend(p,8,2*DAY);moveOvertime(p,8,BuildLibra.PM,2.0/3.0);moveOvertime(p,9,BuildLibra.PM,2.0/3.0);extend(p,37,5*DAY);extend(p,35,5*DAY);moveOvertime(p,36,BuildLibra.BA,.8);moveOvertime(p,37,BuildLibra.BA,.5);moveOvertime(p,15,BuildLibra.TL,.5);moveOvertime(p,19,BuildLibra.TL,.75);moveOvertime(p,21,BuildLibra.TL,.75);moveOvertime(p,16,BuildLibra.TL,.6);moveOvertime(p,35,BuildLibra.TL,2.0/3.0);moveOvertime(p,17,BuildLibra.QA,.5);moveOvertime(p,18,BuildLibra.QA,.5);recalc(p);}
    case 4->{Resource qa=cloneResource(p,BuildLibra.QA,"Тест-инженер 2"),pm=cloneResource(p,BuildLibra.PM,"Руководитель проекта 2");replace(p,9,BuildLibra.PM,pm);replace(p,18,BuildLibra.QA,qa);datesAfter(p,19,15);extend(p,36,Math.round(DAY*20.0/3.0));extend(p,37,8*DAY);moveOvertime(p,36,BuildLibra.BA,5.0/6.0);recalc(p);}
   }
   double initialWork=totalWork(base);BuildLibra.check(Math.abs(totalWork(p)-initialWork)<.01,"Total work changed: "+name+" base="+initialWork+" now="+totalWork(p));
   BuildLibra.check(tasks(p).get(16).getConstraintType()==ConstraintType.ALAP,"ALAP constraint changed");
   List<Conflict>issues=conflicts(p);BuildLibra.check(issues.isEmpty(),"Remaining regular-time overloads: "+name+" "+issues);
   Path out=dir.resolve(name+".pod");save(p,out,"Способ выравнивания: "+name+". Общий объём трудозатрат сохранён. Автоматическое выравнивание выполнено отдельным алгоритмом по данным назначения и календаря (встроенная команда ProjectLibre 1.9.8 не реализована). Перегрузки проверяются отдельно для обычного рабочего времени, сверхурочные оплачиваются по сверхурочным ставкам.");
   Project loaded=InspectLibra.load(out.toString());recalc(loaded);BuildLibra.check(conflicts(loaded).isEmpty(),"Reload shows conflicts: "+name);BuildLibra.check(Math.abs(totalWork(loaded)-initialWork)<.01,"Reload work mismatch");BuildLibra.check(Math.abs(loaded.getCost(null)-p.getCost(null))<.01,"Reload cost mismatch");export(loaded,audits,name);
   summary.append(name).append('\t').append(F.format(Instant.ofEpochMilli(tasks(loaded).get(39).getEnd()))).append('\t').append(loaded.getCost(null)).append('\t').append(totalWork(loaded)).append('\t').append(totalOvertime(loaded)).append('\t').append(conflicts(loaded).size()).append('\t').append(conflicts(loaded).stream().map(Conflict::resource).distinct().count()).append('\n');
   System.out.println("RESULT "+name+" work="+totalWork(loaded)+" overtime="+totalOvertime(loaded)+" cost="+loaded.getCost(null)+" finish="+new Date(tasks(loaded).get(39).getEnd()));
  }
  Files.writeString(dir.resolve("Сравнение.tsv"),summary,StandardCharsets.UTF_8);System.exit(0);
 }
}
