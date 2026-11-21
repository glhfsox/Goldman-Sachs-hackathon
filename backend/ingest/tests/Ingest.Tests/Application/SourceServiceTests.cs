using Ingest.Application.DTOs.Requests;
using Ingest.Application.Interfaces;
using Ingest.Application.Services;
using Ingest.Domain.Models;
using Ingest.Domain.Repositories;
using Ingest.Domain.ValueObjects;

namespace Ingest.Tests.Application;

public sealed class SourceServiceTests
{
    private readonly FakeRepository _repository = new();
    private readonly SourceService _service;

    public SourceServiceTests()
    {
        _service = new SourceService(_repository, _repository);
    }

    [Fact]
    public async Task ApplyChange_UnknownId_InsertsAndSavesOnce()
    {
        var applied = await _service.ApplyChangeAsync(Request("new-http", At(5)));

        Assert.True(applied);
        Assert.Equal("new-http", Assert.Single(_repository.Sources).Id);
        Assert.Equal(1, _repository.Saves);
    }

    [Fact]
    public async Task ApplyChange_NewerRecord_UpdatesTheRow()
    {
        _repository.Sources.Add(Source.Create("s", SourceKind.Http, SourceConfig.Empty, enabled: true, At(1)));

        var applied = await _service.ApplyChangeAsync(Request("s", At(2), enabled: false));

        Assert.True(applied);
        Assert.False(_repository.Sources.Single().Enabled);
        Assert.Equal(1, _repository.Saves);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task ApplyChange_EqualOrOlderRecord_ChangesNothingAndDoesNotSave(int seconds)
    {
        _repository.Sources.Add(Source.Create("s", SourceKind.Http, SourceConfig.Empty, enabled: true, At(2)));

        var applied = await _service.ApplyChangeAsync(Request("s", At(seconds), enabled: false));

        Assert.False(applied);
        Assert.True(_repository.Sources.Single().Enabled);
        Assert.Equal(0, _repository.Saves);
    }

    private static ApplySourceChangeRequest Request(string id, DateTimeOffset updatedAt, bool enabled = true)
    {
        return new ApplySourceChangeRequest(id, SourceKind.Http, SourceConfig.Empty, enabled, updatedAt);
    }

    private static DateTimeOffset At(int seconds)
    {
        return new DateTimeOffset(2026, 11, 21, 0, 0, seconds, TimeSpan.Zero);
    }

    private sealed class FakeRepository : ISourceRepository, IUnitOfWork
    {
        public List<Source> Sources { get; } = [];

        public int Saves { get; private set; }

        public Task<IReadOnlyList<Source>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Source>>(Sources);
        }

        public Task<Source?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sources.SingleOrDefault(source => source.Id == id));
        }

        public Task AddAsync(Source source, CancellationToken cancellationToken = default)
        {
            Sources.Add(source);

            return Task.CompletedTask;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;

            return Task.FromResult(1);
        }
    }
}
