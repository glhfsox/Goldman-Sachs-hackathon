using System.Net;
using System.Net.Sockets;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Ingest.Tests.Infrastructure;

// A single KRaft broker. The testcontainers Kafka module does not configure apache/kafka 4.x,
// and the advertised address must equal the published host port, so a free port is picked first.
public sealed class KafkaFixture : Xunit.IAsyncLifetime
{
    private readonly int _port = GetFreePort();
    private IContainer? _container;

    public string Brokers => $"localhost:{_port}";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder().WithImage("apache/kafka:4.3.1")
            .WithPortBinding(_port, 9094)
            .WithEnvironment("KAFKA_NODE_ID", "1")
            .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
            .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_LISTENERS", "HOST://:9094,CONTROLLER://:9093")
            .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"HOST://localhost:{_port}")
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "HOST:PLAINTEXT,CONTROLLER:PLAINTEXT")
            .WithEnvironment("KAFKA_INTER_BROKER_LISTENER_NAME", "HOST")
            .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
