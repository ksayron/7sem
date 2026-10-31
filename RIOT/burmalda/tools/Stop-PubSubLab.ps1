param()
$ErrorActionPreference='Stop'
foreach($taskComponent in @('Web','Gateway','Identity','GameApi','Subscribers','BrokerB','BrokerA','Coordinator')) {
    & "$PSScriptRoot/Set-PubSubComponent.ps1" -Component $taskComponent -Action Stop
}
# Keep PostgreSQL, RabbitMQ and all durable application data intact.
