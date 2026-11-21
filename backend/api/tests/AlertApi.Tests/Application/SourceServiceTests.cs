using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Interfaces;
using AlertApi.Application.Services;
using AlertApi.Domain.Models;
using AlertApi.Domain.Repositories;

namespace AlertApi.Tests.Application;

public sealed class SourceServiceTests
{
    private readonly FakeRepository _repository = new();
    private readonly FakeOutbox _outbox = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly SourceService _service;

    public SourceServiceTests()
    {
        _service = new SourceService(_repository, _outbox, _unitOfWork, TimeProvider.System);
    }

    [Fact]
    public async Task Create_ValidKafkaSource_StoresItAndQueuesOneRecord()
    {
        var response = await _service.CreateAsync(
            new CreateSourceRequest("orders-kafka", "kafka", new SourceConfigDto("source.orders", null)));

        Assert.True(response.Enabled);
        Assert.Single(_repository.Sources);
        Assert.Equal([("source", "orders-kafka")], _outbox.Records);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task Create_ExistingId_ThrowsConflictAndQueuesNothing()
    {
        await _service.CreateAsync(new CreateSourceRequest("orders-http", "http", null));

        await Assert.ThrowsAsync<SourceAlreadyExistsException>(
            () => _service.CreateAsync(new CreateSourceRequest("orders-http", "http", null)));

        Assert.Single(_outbox.Records);
    }

    [Fact]
    public async Task Create_KafkaWithoutTopicAndBadId_ReportsEveryProblem()
    {
        var exception = await Assert.ThrowsAsync<InvalidSourceException>(
            () => _service.CreateAsync(new CreateSourceRequest("Bad Id", "kafka", null)));

        Assert.Equal("invalid_source", exception.Code);
        Assert.Equal(["config.topic", "id"], exception.Problems.Select(problem => problem.Path).Order());
        Assert.Empty(_outbox.Records);
    }

    [Fact]
    public async Task Create_UnknownKind_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<InvalidSourceException>(
            () => _service.CreateAsync(new CreateSourceRequest("orders-mq", "rabbitmq", null)));

        Assert.Equal("kind", Assert.Single(exception.Problems).Path);
    }

    [Fact]
    public async Task Update_DisablingASource_QueuesTheNewState()
    {
        await _service.CreateAsync(new CreateSourceRequest("orders-http", "http", null));

        var response = await _service.UpdateAsync("orders-http", new UpdateSourceRequest("http", null, Enabled: false));

        Assert.False(response.Enabled);
        Assert.Equal(2, _outbox.Records.Count);
    }

    [Fact]
    public async Task Update_UnknownId_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<SourceNotFoundException>(
            () => _service.UpdateAsync("missing", new UpdateSourceRequest("http", null, Enabled: true)));
    }

    private sealed class FakeRepository : ISourceRepository
    {
        public List<Source> Sources { get; } = [];

        public Task<IReadOnlyList<Source>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Source>>(Sources);
        }

        public Task<Source?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sources.FirstOrDefault(source => source.Id == id));
        }

        public Task AddAsync(Source source, CancellationToken cancellationToken = default)
        {
            Sources.Add(source);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeOutbox : IConfigChangeOutbox
    {
        public List<(string Kind, string Id)> Records { get; } = [];

        public void Add(string kind, string id, DateTimeOffset updatedAt, object body)
        {
            Records.Add((kind, id));
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;

            return Task.FromResult(0);
        }

        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
