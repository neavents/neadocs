namespace Neadocs.Engine.Infrastructure.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Storage;

public sealed class EmbeddingBacklogWorker : BackgroundService
{
    private readonly EmbeddingStore _store;
    private readonly EmbeddingModelRegistry _models;
    private readonly BacklogWorkerOptions _options;
    private readonly ILogger<EmbeddingBacklogWorker> _logger;

    public EmbeddingBacklogWorker(
        EmbeddingStore store,
        EmbeddingModelRegistry models,
        IOptions<DocumentEngineOptions> options,
        ILogger<EmbeddingBacklogWorker> logger)
    {
        _store = store;
        _models = models;
        _options = options.Value.BacklogWorker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_models.HasActiveModel)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The embedding backlog worker failed a pass; it will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task DrainAsync(CancellationToken ct)
    {
        foreach (EmbeddingModelDescriptor model in _models.Active)
        {
            long depth = await _store.BacklogDepthAsync(model.Slug, ct);
            NeadocsMeters.SetBacklogDepth(model.Slug, depth);

            if (depth == 0)
            {
                continue;
            }

            List<PendingEmbedding> due = await _store.DueBacklogAsync(
                model.Slug, _options.BatchSize, _options.MaxAttempts, ct);

            if (due.Count == 0)
            {
                continue;
            }

            _logger.LogInformation(
                "Retrying {Count} backlogged chunk(s) for model {Model}; backlog depth {Depth}.",
                due.Count, model.Slug, depth);

            await _store.EmbedAsync(due, ct);

            NeadocsMeters.SetBacklogDepth(model.Slug, await _store.BacklogDepthAsync(model.Slug, ct));
        }
    }
}
