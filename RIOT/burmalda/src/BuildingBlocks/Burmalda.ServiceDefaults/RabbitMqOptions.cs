namespace Burmalda.ServiceDefaults;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "burmalda";
    public string Password { get; set; } = "burmalda_dev";
    public string VirtualHost { get; set; } = "/";
}

public sealed record QueueBinding(string Exchange, string RoutingKey);

public interface IMessageQueueDefinition
{
    string QueueName { get; }
    IReadOnlyCollection<QueueBinding> Bindings { get; }
}
