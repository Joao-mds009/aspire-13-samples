using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Api.Data;
using Api.Models;
using System.Text.Json;

namespace Api.Extensions;

public static class ImageEndpoints
{
    public static WebApplication MapImages(this WebApplication app)
    {
        var group = app.MapGroup("/api");

        // Get all images
        group.MapGet("/images", async (ImageDbContext db) =>
        {
            var images = await db.Images.OrderByDescending(i => i.UploadedAt).ToListAsync();
            return images.Select(ImageDto.FromImage).ToList();
        });

        // Get image by id
        group.MapGet("/images/{id}", async (int id, ImageDbContext db) =>
        {
            var image = await db.Images.FindAsync(id);
            return image is not null ? Results.Ok(ImageDto.FromImage(image)) : Results.NotFound();
        });

        // Serve image blob
        group.MapGet("/images/{id}/blob", async (
            int id,
            ImageDbContext db,
            BlobContainerClient containerClient) =>
        {
            var image = await db.Images.FindAsync(id);
            if (image is null)
            {
                return Results.NotFound();
            }

            var blobName = image.BlobUrl.Split('/').Last();
            var blobClient = containerClient.GetBlobClient(blobName);
            
            var download = await blobClient.DownloadStreamingAsync();
            return Results.Stream(download.Value.Content, image.ContentType);
        });

        // Serve thumbnail blob
        group.MapGet("/images/{id}/thumbnail", async (
            int id,
            ImageDbContext db,
            BlobContainerClient containerClient) =>
        {
            var image = await db.Images.FindAsync(id);
            if (image is null || image.ThumbnailUrl is null)
            {
                return Results.NotFound();
            }

            var thumbnailName = image.ThumbnailUrl.Split('/').Last();
            var blobClient = containerClient.GetBlobClient(thumbnailName);
            
            var download = await blobClient.DownloadStreamingAsync();
            return Results.Stream(download.Value.Content, image.ContentType);
        });

        // Upload image
        group.MapPost("/images", async (
            IFormFile file,
            ImageDbContext db,
            BlobContainerClient containerClient,
            QueueServiceClient queueService,
            ILogger<Program> logger) =>
        {
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No file uploaded" });
            }

            // Validate content type
            if (!file.ContentType.StartsWith("image/"))
            {
                return Results.BadRequest(new { error = "File must be an image" });
            }

            try
            {
                // Get container and queue clients
                var queueClient = queueService.GetQueueClient("thumbnails");
                await queueClient.CreateIfNotExistsAsync();

                // Generate unique blob name
                var blobName = $"{Guid.NewGuid()}-{file.FileName}";
                var blobClient = containerClient.GetBlobClient(blobName);

                // Upload to blob storage
                using var stream = file.OpenReadStream();
                await blobClient.UploadAsync(stream, overwrite: true);

                // Save metadata to database
                var image = new Image
                {
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    Size = file.Length,
                    BlobUrl = blobClient.Uri.ToString(),
                    ThumbnailProcessed = false
                };

                db.Images.Add(image);
                await db.SaveChangesAsync();

                // Queue thumbnail generation
                var message = JsonSerializer.Serialize(new
                {
                    imageId = image.Id,
                    blobName = blobName
                });
                await queueClient.SendMessageAsync(message);

                logger.LogInformation("Image {ImageId} uploaded: {FileName}, queued for thumbnail generation",
                    image.Id, file.FileName);

                return Results.Created($"/api/images/{image.Id}", ImageDto.FromImage(image));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload image");
                return Results.Problem("Failed to upload image");
            }
        })
        .DisableAntiforgery(); // Required for file uploads

        // Delete image
        group.MapDelete("/images/{id}", async (
            int id,
            ImageDbContext db,
            BlobContainerClient containerClient,
            ILogger<Program> logger) =>
        {
            var image = await db.Images.FindAsync(id);
            if (image is null)
            {
                return Results.NotFound();
            }

            try
            {
                // Delete from blob storage
                var blobName = image.BlobUrl.Split('/').Last();
                await containerClient.DeleteBlobIfExistsAsync(blobName);

                // Delete thumbnail if exists
                if (image.ThumbnailUrl is not null)
                {
                    var thumbnailName = image.ThumbnailUrl.Split('/').Last();
                    await containerClient.DeleteBlobIfExistsAsync(thumbnailName);
                }

                // Delete from database
                db.Images.Remove(image);
                await db.SaveChangesAsync();

                logger.LogInformation("Image {ImageId} deleted: {FileName}", id, image.FileName);

                return Results.Ok(new { message = $"Image {id} deleted" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete image {ImageId}", id);
                return Results.Problem("Failed to delete image");
            }
        });

        return app;
    }
}
