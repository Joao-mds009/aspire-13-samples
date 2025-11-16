using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;
using Worker.Data;

namespace Worker.Services;

public class ThumbnailWorker(
    IServiceProvider serviceProvider,
    BlobContainerClient containerClient,
    QueueServiceClient queueService,
    ILogger<ThumbnailWorker> logger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly BlobContainerClient _containerClient = containerClient;
    private readonly QueueServiceClient _queueService = queueService;
    private readonly ILogger<ThumbnailWorker> _logger = logger;
    private const int ThumbnailWidth = 300;
    private const int ThumbnailHeight = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Thumbnail worker started");

        var queueClient = _queueService.GetQueueClient("thumbnails");
        await queueClient.CreateIfNotExistsAsync(cancellationToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Receive messages from queue
                QueueMessage[] messages = await queueClient.ReceiveMessagesAsync(
                    maxMessages: 10,
                    visibilityTimeout: TimeSpan.FromMinutes(5),
                    cancellationToken: stoppingToken);

                if (messages.Length == 0)
                {
                    // No messages, wait before polling again
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    try
                    {
                        await ProcessMessageAsync(message, queueClient, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process message: {MessageId}", message.MessageId);
                        // Message will become visible again after visibility timeout
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in thumbnail worker main loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("Thumbnail worker stopped");
    }

    private async Task ProcessMessageAsync(
        QueueMessage message,
        QueueClient queueClient,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        // Parse message
        var data = JsonSerializer.Deserialize<JsonElement>(message.MessageText);
        var imageId = data.GetProperty("imageId").GetInt32();
        var blobName = data.GetProperty("blobName").GetString()!;

        _logger.LogInformation("Processing thumbnail for image {ImageId}, blob: {BlobName}", imageId, blobName);

        var sourceBlobClient = _containerClient.GetBlobClient(blobName);

        // Download original image
        using var originalStream = new MemoryStream();
        await sourceBlobClient.DownloadToAsync(originalStream, cancellationToken);
        originalStream.Position = 0;

        // Generate thumbnail
        using var image = await Image.LoadAsync(originalStream, cancellationToken);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(ThumbnailWidth, ThumbnailHeight),
            Mode = ResizeMode.Max
        }));

        // Upload thumbnail
        var thumbnailName = $"thumb-{blobName}";
        var thumbnailBlobClient = containerClient.GetBlobClient(thumbnailName);

        using var thumbnailStream = new MemoryStream();
        await image.SaveAsJpegAsync(thumbnailStream, cancellationToken);
        thumbnailStream.Position = 0;
        await thumbnailBlobClient.UploadAsync(thumbnailStream, overwrite: true, cancellationToken: cancellationToken);

        // Update database
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ImageDbContext>();
        var imageRecord = await db.Images.FindAsync([imageId], cancellationToken: cancellationToken);

        if (imageRecord != null)
        {
            imageRecord.ThumbnailUrl = thumbnailBlobClient.Uri.ToString();
            imageRecord.ThumbnailProcessed = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Delete message from queue
        await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);

        var processingTime = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Thumbnail generated for image {ImageId} in {ProcessingTime}ms",
            imageId,
            processingTime.TotalMilliseconds);
    }
}
