import com.projectlibre1.util.Environment;
import com.projectlibre1.configuration.Configuration;
import com.projectlibre1.exchange.LocalFileImporter;
import com.projectlibre1.pm.task.*;
import com.projectlibre1.pm.resource.*;
import java.util.*;

public class InspectLibra {
 public static void init() {
  java.util.logging.LogManager.getLogManager().reset();
  System.setProperty("java.awt.headless", "true");
  Environment.setStandAlone(true); Environment.setBatchMode(true);
  Environment.setClientSide(true); Environment.setVisible(false);
  Configuration.getInstance();
 }
 public static Project load(String path) throws Exception {
  LocalFileImporter i = new LocalFileImporter(); i.setFileName(path); i.importFile();
  if(i.getProject()==null) throw new IllegalStateException("Project load failed");
  return i.getProject();
 }
 public static void main(String[] args) throws Exception {
  init(); Project p=load(args[0]);
  System.out.println("PROJECT "+p.getName()+" tasks="+p.getTasks().size()+" resources="+p.getResourcePool().getResourceList().size());
  for(Object x:p.getTasks()) { NormalTask t=(NormalTask)x;
   System.out.println("TASK "+t.getId()+" uid="+t.getUniqueId()+" "+t.getName()+" duration="+t.getDuration()+" start="+t.getStart()+" end="+t.getEnd()+" effort="+t.isEffortDriven()+" type="+t.getSchedulingType()+" assignments="+t.getAssignments().size());
  }
  System.exit(0);
 }
}
