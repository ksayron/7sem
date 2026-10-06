import com.projectlibre1.pm.task.*;
import com.projectlibre1.pm.resource.*;
import com.projectlibre1.pm.assignment.*;
import com.projectlibre1.pm.costing.*;
import com.projectlibre1.datatype.*;
import com.projectlibre1.exchange.LocalFileImporter;
import com.projectlibre1.server.data.MSPDISerializer;
import java.util.*;
import java.nio.file.*;
import java.nio.charset.StandardCharsets;

public class BuildLibra {
 static final String PM="Руководитель проекта",P1="Программист 1",P2="Программист 2",P3="Программист 3",BA="Бизнес-аналитик",QA="Тест-инженер",TL="Технический лидер",SA="Системный аналитик";
 static final String PC="Компьютер",SERVER="Сервер",PRINTER="Принтер",LAPTOP="Ноутбук",MFP="МФУ",TABLET="Планшет",OS="Серверная ОС",PAPER="Бумага для принтера",INK="Картридж для принтера",NET="Интернет";
 record R(String name,int type,double std,double ot,double use,String label,String group){}
 record A(int task,String name,double units){}
 static List<R> resources=List.of(
  new R(PM,1,15,20,0,"","A"), new R(P1,1,14,18,0,"","A"),new R(P2,1,15,20,0,"","A"),new R(P3,1,12,16,0,"","A"),
  new R(BA,1,11,14,0,"","AB"),new R(QA,1,8,10,0,"","A"),new R(TL,1,17,22,0,"","A"),
  new R(PC,0,2780,0,0,"шт.","A"),new R(SERVER,0,3950,0,0,"шт.","A"),new R(PRINTER,0,315,0,0,"шт.","A"),
  new R(LAPTOP,0,2450,0,0,"шт.","A"),new R(MFP,0,1120,0,0,"шт.","A"),new R(TABLET,0,1780,0,0,"шт.","A"),
  new R(OS,0,0,0,3130,"ед.","A"),new R(PAPER,0,15,0,0,"уп.","A"),new R(INK,0,115,0,0,"ед.","A"),
  new R(NET,0,50,0,0,"мес.",""),new R(SA,1,11,14,0,"","A")
 );
 static List<A> assignments=new ArrayList<>();
 static void a(int task,Object... pairs){for(int i=0;i<pairs.length;i+=2)assignments.add(new A(task,(String)pairs[i],((Number)pairs[i+1]).doubleValue()));}
 static void definitions(){
  a(3,BA,1,TL,1,PM,1,TABLET,2,LAPTOP,1);
  a(4,BA,1,TL,1,LAPTOP,1,NET,.2,MFP,1,PAPER,1);
  a(5,QA,1);a(6,BA,1,PM,1);
  a(7,PM,1,LAPTOP,1,PRINTER,1,INK,1,PAPER,1);
  a(8,PM,1);a(9,PM,1);
  a(12,BA,1,TL,1,PC,1,INK,1,PAPER,1,NET,.24);
  a(14,BA,.3,SA,.5,PM,.5,P1,.5,P2,.5,P3,.5,QA,.5);
  a(15,TL,.5);a(16,TL,1,QA,1);
  a(17,TL,.5,P1,.5,P2,.5,P3,.5,QA,.5);
  a(18,QA,.5);a(19,BA,.4,TL,1,PM,1);
  a(21,P1,1,P2,1,P3,1,TL,1);
  a(23,P1,1,P2,1,P3,1,SERVER,1,OS,.5);
  a(24,TL,1);a(22,P1,1,P2,1,P3,1,TL,1);
  a(39,BA,1,TL,1,PM,1);
  a(25,TL,1,QA,1);a(26,QA,1);a(29,TL,1,QA,1);a(30,TL,1);
  a(31,P1,1,P2,1,P3,1);a(34,TL,1);a(35,BA,1,INK,1,PAPER,1);a(36,BA,1);
 }
 static void check(boolean b,String m){if(!b)throw new IllegalStateException(m);}
 public static void main(String[] args)throws Exception{
  InspectLibra.init(); definitions();
  assignments.replaceAll(x->new A(PlanLibra.OLD_TO_NEW[x.task()],x.name(),x.units()));
  Project p=PlanLibra.create(InspectLibra.load(args[0]));
  Map<Integer,NormalTask> tasks=new TreeMap<>();Map<Integer,long[]> dates=new TreeMap<>();
  for(Object x:p.getTasks()){NormalTask t=(NormalTask)x;tasks.put((int)t.getId(),t);dates.put((int)t.getId(),new long[]{t.getStart(),t.getEnd(),t.getDuration()});}
  Map<String,Resource> resourceMap=new LinkedHashMap<>();
  for(R spec:resources){
   Resource r=p.getResourcePool().createScriptedResource();r.setName(spec.name());r.setResourceType(spec.type());((ResourceImpl)r).setMaximumUnits(1);
   r.setGroup(spec.group());r.setMaterialLabel(spec.label());r.setBaseCalendar(p.getWorkCalendar());
   int rateUnit=spec.type()==1?TimeUnit.HOURS:TimeUnit.NON_TEMPORAL;
   double divisor=spec.type()==1?3600000.0:1.0;
   r.setStandardRate(new Rate(spec.std()/divisor,rateUnit));r.setOvertimeRate(new Rate(spec.ot()/divisor,rateUnit));r.setCostPerUse(spec.use());
   if(spec.name().equals(BA)){
    CostRate cr=(CostRate)r.getCostRateTable(1).findCurrent();
    cr.setStandardRate(new Rate(11/3600000.0,TimeUnit.HOURS));cr.setOvertimeRate(new Rate(14/3600000.0,TimeUnit.HOURS));cr.setCostPerUse(0);
    r.setNotes("Таблица 2.6: нормы затрат A и B. Ставки обеих таблиц: 11 руб./ч, сверхурочная 14 руб./ч; разные ставки B в задании не указаны. Назначения по таблице A.");
   }
   if(spec.name().equals(SA))r.setNotes("Отсутствует в таблице 2.6, присутствует в таблице 2.7. По указанию пользователя ставки как у бизнес-аналитика: 11 и 14 руб./ч.");
   if(spec.name().equals(NET))r.setNotes("По заданию тип З (затраты), 50 руб./мес. В ProjectLibre 1.9.8 отдельного типа Cost нет: эквивалент материальный ресурс, единица мес., ставка 50 руб. Назначения 0,2 мес. = 10 руб.; 0,24 мес. = 12 руб. Никакие дополнительные ежемесячные расходы не начислены.");
   resourceMap.put(spec.name(),r);
  }
  Map<Integer,List<A>> grouped=new LinkedHashMap<>();for(A x:assignments)grouped.computeIfAbsent(x.task(),k->new ArrayList<>()).add(x);
  for(var entry:grouped.entrySet()){
   NormalTask t=tasks.get(entry.getKey());check(t!=null,"Task not found: "+entry.getKey());
   Assignment template=(Assignment)t.getAssignments().get(0);
   List<Assignment> replacements=new ArrayList<>();
   for(A spec:entry.getValue()){
    Resource r=resourceMap.get(spec.name());
    Assignment assignment=(Assignment)template.cloneWithResourceAndTask(r,t);
    assignment.setUniqueId(1000+assignments.indexOf(spec));assignment.setCostRateIndex(0);
    assignment.getRate().setValue(spec.units());
    assignment.getRate().setTimeUnit(r.isLabor()?TimeUnit.PERCENT:TimeUnit.NON_TEMPORAL);
    replacements.add(assignment);
   }
   for(Object x:new ArrayList(t.getAssignments())){Assignment old=(Assignment)x;old.getResource().removeAssignment(old);}
   t.getAssignments().clear();
   for(Assignment assignment:replacements){t.getAssignments().add(assignment);assignment.getResource().addAssignment(assignment);}
  }
  for(var entry:tasks.entrySet()){long[] d=dates.get(entry.getKey());NormalTask t=entry.getValue();check(t.getStart()==d[0]&&t.getEnd()==d[1]&&t.getDuration()==d[2],"Resource assignment changed schedule for task "+t.getId());}
  String notes="Вариант 1, Разработка ПО. 39 задач, порядок, длительности и предшественники по таблице 2.3, предоставленной пользователем. Пять этапов оформлены как суммарные задачи. Ресурсы таблицы 2.6, назначения таблицы 2.7 (задания 8 и 9). Системный аналитик: 11/14 руб./ч по указанию пользователя. Интернет: эквивалент затрат 10 и 12 руб. через материальный ресурс 50 руб./мес. (0,2 и 0,24 мес.). Бизнес-аналитик: нормы A и B с одинаковыми заданными ставками. Исходный рабочий календарь сохранён, начало 18.09.2026 09:00. Зависимости окончание-начало, без задержек. Сроки пересчитаны по исправленной сети. Суммы и ставки в рублях; символ валюты в интерфейсе определяется настройками ProjectLibre.";
  p.setNotes((p.getNotes()==null?"":p.getNotes()+"\n")+notes);
  LocalFileImporter exporter=new LocalFileImporter();exporter.setProject(p);exporter.setFileName(args[1]);exporter.exportFile();
  check(new MSPDISerializer().saveProject(p,args[2]),"XML export failed");
  StringBuilder report=new StringBuilder("task_id\ttask\tresource\tunits\tcost\twork_ms\n");
  for(A spec:assignments){NormalTask t=tasks.get(spec.task());Assignment v=t.findAssignment(resourceMap.get(spec.name()));check(v!=null,"Assignment missing");
   check(Math.abs(v.getRate().getValue()-spec.units())<1e-9,"Wrong assignment units");
   report.append(spec.task()).append('\t').append(t.getName().trim()).append('\t').append(spec.name()).append('\t').append(v.getRate().getValue()).append('\t').append(v.getCost()).append('\t').append(v.getWork(null)).append('\n');
  }
  Files.writeString(Path.of(args[3]),report,StandardCharsets.UTF_8);
  System.out.println("RESULT resources="+resources.size()+" assignments="+assignments.size()+" tasks="+tasks.size());
  System.exit(0);
 }
}
