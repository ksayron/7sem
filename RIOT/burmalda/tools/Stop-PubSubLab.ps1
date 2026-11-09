param([switch]$Local)
$ErrorActionPreference='Stop'
if(!$Local){
    & "$PSScriptRoot/Stop-Lab.ps1"
    return
}
foreach($taskComponent in @('Web','Gateway','Identity','GameApi','Subscribers','BrokerB','BrokerA','Coordinator')) {
    & "$PSScriptRoot/Set-PubSubComponent.ps1" -Local -Component $taskComponent -Action Stop
}
# Keep PostgreSQL, RabbitMQ and all durable application data intact.
& "$PSScriptRoot/Stop-RaftCluster.ps1" -Local
