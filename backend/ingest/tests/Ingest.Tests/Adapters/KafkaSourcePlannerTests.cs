using Microsoft.Extensions.Logging.Abstractions;

using Ingest.Domain.Models;
using Ingest.Domain.ValueObjects;
using Ingest.Infrastructure.Adapters;

namespace Ingest.Tests.Adapters;

public sealed class KafkaSourcePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_GroupsEnabledKafkaSourcesByBrokersAndDefaultsTheBrokers()
    {
        var sources = new[]
        {
            Kafka("workflows-kafka", "source.workflows"),
            Kafka("trades-kafka", "source.trades"),
            Kafka("other-kafka", "source.other", "other:9092"),
            Kafka("off-kafka", "source.off", enabled: false),
            Source.Create("hook-http", SourceKind.Http, SourceConfig.Empty, enabled: true, Now)
        };

        var plan = KafkaSourcePlanner.Plan(sources, "default:9092", NullLogger.Instance);

        Assert.Equal(["default:9092", "other:9092"], plan.Keys.Order());
        Assert.Equal("workflows-kafka", plan["default:9092"]["source.workflows"]);
        Assert.Equal("trades-kafka", plan["default:9092"]["source.trades"]);
        Assert.Equal(2, plan["default:9092"].Count);
        Assert.Equal("other-kafka", plan["other:9092"]["source.other"]);
    }

    [Fact]
    public void Plan_SameTopicOnSameBrokers_FirstByIdWins()
    {
        var sources = new[]
        {
            Kafka("b-kafka", "source.shared"),
            Kafka("a-kafka", "source.shared")
        };

        var plan = KafkaSourcePlanner.Plan(sources, "default:9092", NullLogger.Instance);

        Assert.Equal("a-kafka", plan["default:9092"]["source.shared"]);
        Assert.Single(plan["default:9092"]);
    }

    [Fact]
    public void Plan_SameTopicOnOtherBrokers_IsNotAClash()
    {
        var sources = new[]
        {
            Kafka("a-kafka", "source.shared"),
            Kafka("b-kafka", "source.shared", "other:9092")
        };

        var plan = KafkaSourcePlanner.Plan(sources, "default:9092", NullLogger.Instance);

        Assert.Equal("a-kafka", plan["default:9092"]["source.shared"]);
        Assert.Equal("b-kafka", plan["other:9092"]["source.shared"]);
    }

    private static Source Kafka(string id, string topic, string? brokers = null, bool enabled = true)
    {
        return Source.Create(id, SourceKind.Kafka, new SourceConfig(topic, brokers), enabled, Now);
    }
}
