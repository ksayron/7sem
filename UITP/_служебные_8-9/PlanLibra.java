import com.projectlibre1.pm.task.*;
import com.projectlibre1.pm.resource.*;
import com.projectlibre1.pm.dependency.*;
import com.projectlibre1.grouping.core.*;
import com.projectlibre1.grouping.core.model.NodeModel;
import java.util.*;

public class PlanLibra {
 record T(String name,int days,int parent,int...predecessors){}
 static final List<T> SPECS=List.of(
 new T("Начало проекта",0,0),
 new T("Анализ и сбор требований",-1,0),
 new T("Встреча с заказчиком",1,2,1),
 new T("Написание документа требований",10,2,3),
 new T("Тестирование требований",3,2,4),
 new T("Встреча с заказчиком",1,2,5),
 new T("Подготовка договора и устава проекта",4,2,6),
 new T("Согласование договора",1,2,7),
 new T("Согласование устава проекта",2,2,7),
 new T("Утверждение договора и устава проекта",0,2,8,9),
 new T("Проектирование решения",-1,0),
 new T("Написание и согласование ТЗ",15,11,10),
 new T("Утверждение ТЗ",0,11,12),
 new T("Обучение специалистов предметной области",4,11,13),
 new T("Проектирование архитектуры решения",5,11,14),
 new T("Тестирование архитектуры решения",5,11,15),
 new T("Установка средств разработки и тестирования",4,11,13),
 new T("Написание тест-плана и вариантов тестирования системы",6,11,13),
 new T("Встреча с заказчиком",1,11,18),
 new T("Разработка и внутреннее тестирование",-1,0),
 new T("Встреча разработчиков, декомпозиция задач, назначение ответственных",1,20,19),
 new T("Программирование",45,20,21),
 new T("Улучшение кода",11,20,22),
 new T("Встреча разработчиков для предварительной демонстрации",1,20,23),
 new T("Демонстрация заказчику",1,20,24),
 new T("Первая установка решения в среду тестирования",5,20,25),
 new T("Прохождение тест-кейсов",12,20,26),
 new T("Внутреннее тестирование",0,20,27),
 new T("Тестирование на стороне заказчика",-1,0),
 new T("Первая установка в тестовую среду заказчика",2,29,28),
 new T("Поставки бета-версий",4,29,30),
 new T("Доработка и исправление неисправностей",10,29,31),
 new T("Прохождение тестирования на стороне заказчика",0,29,32),
 new T("Внедрение",-1,0),
 new T("Установка на рабочий сервер",3,34,33),
 new T("Написание инструкций",5,34,33),
 new T("Обучение пользователей",2,34,33),
 new T("Завершение внедрения",0,34,35,36,37),
 new T("Конец проекта",0,0,38));
 static final int[] OLD_TO_NEW={0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,24,22,23,26,27,28,29,30,31,32,33,34,35,36,37,38,39,25};
 static Project create(Project original)throws Exception{
  ResourcePool pool=ResourcePool.createRourcePool("Разработка ПО",null);pool.setLocal(true);pool.setMaster(true);
  Project p=Project.createProject(pool,null);p.setLocal(true);p.setMaster(true);p.setName("Разработка ПО");p.setWorkCalendar(original.getWorkCalendar());p.setCalendarOption(original.getCalendarOption());
  p.setStartDate(original.getStart());p.setStartConstraint(original.getStart());
  Map<Integer,NormalTask>tasks=new LinkedHashMap<>();Map<Integer,Node>nodes=new LinkedHashMap<>();NodeModel outline=p.getTaskOutline();
  for(int id=1;id<=39;id++){
   T spec=SPECS.get(id-1);NormalTask t=p.newStandaloneNormalTaskInstance();t.setName(spec.name());t.setId(id);t.setRawConstraintType(0);t.setConstraintDate(0);t.setMarkTaskAsMilestone(spec.days()==0);t.setEffortDriven(false);t.setSchedulingType(1);
   p.connectTask(t);Node n=NodeFactory.getInstance().createNode(t);outline.add(spec.parent()==0?null:nodes.get(spec.parent()),n,0);p.setLocalParent(t,spec.parent()==0?null:tasks.get(spec.parent()));
   if(spec.days()>=0)t.setDuration(spec.days()*28800000L);
   tasks.put(id,t);nodes.put(id,n);
  }
  for(int id=1;id<=39;id++)for(int pred:SPECS.get(id-1).predecessors()){Dependency dep=Dependency.getInstance(tasks.get(pred),tasks.get(id),DependencyType.FS,0);dep.updateDependencyLists();}
  p.initialize(false,false);p.markAllTasksAsNeedingRecalculation(true);p.getSchedulingAlgorithm().calculate(true);
  for(var entry:tasks.entrySet()){int id=entry.getKey();NormalTask t=entry.getValue();T spec=SPECS.get(id-1);if(spec.days()>=0)BuildLibra.check(com.projectlibre1.datatype.Duration.millis(t.getDuration())==spec.days()*28800000L,"Wrong plan duration: "+id);}
  return p;
 }
 public static void main(String[]args)throws Exception{InspectLibra.init();Project p=create(InspectLibra.load(args[0]));for(Object x:p.getTasks()){NormalTask t=(NormalTask)x;System.out.println(t.getId()+" "+t.getName()+" days="+com.projectlibre1.datatype.Duration.millis(t.getDuration())/28800000.0+" pred="+t.getPredecessors()+" summary="+t.isWbsParent()+" start="+new Date(t.getStart())+" finish="+new Date(t.getEnd()));}System.exit(0);}
}
