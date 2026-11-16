import jenkins.model.Jenkins
import hudson.security.*
import hudson.model.Node
import hudson.slaves.*
import hudson.plugins.git.*
import hudson.plugins.git.extensions.impl.PathRestriction
import hudson.plugins.git.extensions.impl.CloneOption
import hudson.plugins.git.extensions.impl.SparseCheckoutPaths
import hudson.plugins.git.extensions.impl.SparseCheckoutPath
import org.jenkinsci.plugins.workflow.job.WorkflowJob
import org.jenkinsci.plugins.workflow.cps.CpsScmFlowDefinition

def j = Jenkins.get()
j.setNumExecutors(0)
def realm = j.securityRealm instanceof HudsonPrivateSecurityRealm ? j.securityRealm : new HudsonPrivateSecurityRealm(false)
realm.createAccount('admin', new File('/run/ci/admin-password').text.trim())
j.setSecurityRealm(realm)
def authorization = new FullControlOnceLoggedInAuthorizationStrategy()
authorization.setAllowAnonymousRead(false)
j.setAuthorizationStrategy(authorization)
if (j.getNode('burmalda-linux') == null) {
    j.addNode(new DumbSlave('burmalda-linux', '/home/jenkins/agent', new JNLPLauncher()))
}
def node = j.getNode('burmalda-linux')
node.setNumExecutors(1)
node.setLabelString('burmalda-linux')
node.setMode(Node.Mode.EXCLUSIVE)
node.save()
def job = j.getItem('burmalda-lab10') ?: j.createProject(WorkflowJob, 'burmalda-lab10')
def scm = new GitSCM('https://github.com/ksayron/7sem.git')
scm.branches = [new BranchSpec('*/' + (System.getenv('BURMALDA_CI_BRANCH') ?: 'master'))]
scm.extensions.add(new PathRestriction('RIOT/burmalda/.*', ''))
scm.extensions.add(new CloneOption(true, true, '/opt/ci/git-cache.git', 20))
scm.extensions.add(new SparseCheckoutPaths([new SparseCheckoutPath('RIOT/burmalda')]))
job.setDefinition(new CpsScmFlowDefinition(scm, 'RIOT/burmalda/Jenkinsfile'))
job.save()
j.save()
