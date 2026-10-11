using System.IO.Compression;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
const long maxBytes = 20 * 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxBytes + 65536);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBytes + 65536);
builder.Services.AddDbContext<LibraryDb>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("FileLibrary")));
builder.Services.AddSingleton(new FileContentStore(builder.Configuration));
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LibraryDb>();
    await db.Database.EnsureCreatedAsync();
}

var contentStore = app.Services.GetRequiredService<FileContentStore>();

app.UseExceptionHandler();
if (allowedOrigins.Length > 0) app.UseCors();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

app.MapGet("/api/health", async (LibraryDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Problem("SQL Server is unavailable. Check the connection string and initialize the database.", statusCode: 503));

app.MapGet("/api/files", async (LibraryDb db, string? search, string? kind, int? page, CancellationToken ct) =>
{
    var currentPage = Math.Clamp(page ?? 1, 1, 100000);
    var query = db.Files.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        if (term.Length > 200) return Results.BadRequest(new { message = "Search must be 200 characters or fewer." });
        query = query.Where(f => f.Name.Contains(term));
    }
    if (kind is "word" or "pdf" or "image") query = query.Where(f => f.Kind == kind);
    var total = await query.CountAsync(ct);
    var items = await query.OrderByDescending(f => f.UploadedAt).ThenBy(f => f.Id)
        .Skip((currentPage - 1) * 20).Take(20)
        .Select(f => new { f.Id, f.Name, f.ContentType, f.Kind, f.Size, f.UploadedAt }).ToListAsync(ct);
    return Results.Ok(new { items, total, page = currentPage, pageSize = 20 });
});

app.MapPost("/api/files", async (HttpRequest request, LibraryDb db, FileContentStore files, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { message = "Send a multipart form with a file field." });
    IFormCollection form;
    try { form = await request.ReadFormAsync(ct); }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
    { return Results.Json(new { message = "The file exceeds the 20 MB upload limit." }, statusCode: 413); }
    catch (InvalidDataException) { return Results.BadRequest(new { message = "Invalid upload or file exceeds the 20 MB limit." }); }
    if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
        return Results.BadRequest(new { message = "Upload exactly one file using the file field." });
    if (file.Length is <= 0 or > maxBytes)
        return Results.BadRequest(new { message = "Choose a non-empty file up to 20 MB." });
    var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
        return Results.BadRequest(new { message = "Use a filename of 1–200 characters without control characters." });
    var extension = Path.GetExtension(name).ToLowerInvariant();
    await using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer, ct);
    var bytes = buffer.ToArray();
    var format = FileValidation.Identify(extension, bytes);
    if (format is null)
        return Results.BadRequest(new { message = "Unsupported file or contents do not match its extension. Use DOC, DOCX, PDF, JPG, PNG, GIF or WebP." });
    var record = new StoredFile
    {
        Id = Guid.NewGuid(), Name = name, ContentType = format.Value.ContentType,
        Kind = format.Value.Kind, Size = bytes.LongLength, UploadedAt = DateTimeOffset.UtcNow,
        Content = files.UsesBlobStorage ? [] : bytes
    };
    await files.StoreAsync(record.Id, bytes, record.ContentType, ct);
    db.Files.Add(record);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch
    {
        try { await files.DeleteAsync(record.Id, CancellationToken.None); }
        catch (Exception cleanupError)
        {
            app.Logger.LogError(cleanupError, "Could not remove orphaned blob {FileId} after metadata save failed.", record.Id);
        }
        throw;
    }
    return Results.Created($"/api/files/{record.Id}/download", new
    { record.Id, record.Name, record.ContentType, record.Kind, record.Size, record.UploadedAt });
});

app.MapGet("/api/files/{id:guid}/download", async (Guid id, LibraryDb db, FileContentStore files, CancellationToken ct) =>
{
    var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
    return file is null ? Results.NotFound(new { message = "File not found." })
        : Results.File(await files.DownloadAsync(file, ct), file.ContentType, file.Name, enableRangeProcessing: true);
});

// Redirect root to the health endpoint to avoid a 404 when browsing to '/'.
app.MapGet("/", () => Results.Redirect("/api/health"));
// Attempt to connect to the database at startup and write the status to the console.
// This uses the connection string from appsettings.json via the registered LibraryDb.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDb>();
    try
    {
        var can = await db.Database.CanConnectAsync();
        Console.WriteLine(can ? "Database connection: SUCCESS" : "Database connection: FAILED");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database connection: ERROR - {ex.GetType().Name}: {ex.Message}");
    }
}

app.Run();

public sealed class LibraryDb(DbContextOptions<LibraryDb> options) : DbContext(options)
{
    public DbSet<StoredFile> Files => Set<StoredFile>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var file = model.Entity<StoredFile>();
        file.ToTable("Files");
        file.HasKey(f => f.Id);
        file.Property(f => f.Name).HasMaxLength(200).IsRequired();
        file.Property(f => f.ContentType).HasMaxLength(100).IsRequired();
        file.Property(f => f.Kind).HasMaxLength(10).IsRequired();
        file.Property(f => f.Content).HasColumnType("varbinary(max)").IsRequired();
        file.HasIndex(f => f.UploadedAt);
    }
}
public sealed class StoredFile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public sealed class FileContentStore
{
    private readonly BlobContainerClient? _container;
    public bool UsesBlobStorage => _container is not null;

    public FileContentStore(IConfiguration configuration)
    {
        var serviceUri = configuration["BlobServiceUri"];
        if (string.IsNullOrWhiteSpace(serviceUri)) return;

        var containerName = configuration["BlobContainerName"] ?? "files";
        _container = new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential())
            .GetBlobContainerClient(containerName);
    }

    public async Task StoreAsync(Guid id, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        if (_container is null) return;

        var blob = _container.GetBlobClient(id.ToString("N"));
        await blob.UploadAsync(BinaryData.FromBytes(content), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_container is not null)
            await _container.GetBlobClient(id.ToString("N")).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<byte[]> DownloadAsync(StoredFile file, CancellationToken cancellationToken)
    {
        if (file.Content.Length > 0) return file.Content;
        if (_container is null)
            throw new InvalidOperationException($"File {file.Id} has no SQL content and Blob Storage is not configured.");

        var response = await _container.GetBlobClient(file.Id.ToString("N")).DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToArray();
    }
}

public static class FileValidation
{
    public static (string ContentType, string Kind)? Identify(string extension, byte[] bytes)
    {
        bool Starts(byte[] prefix) => bytes.AsSpan().StartsWith(prefix);
        return extension switch
        {
            ".pdf" when Starts("%PDF-"u8.ToArray()) => ("application/pdf", "pdf"),
            ".png" when Starts(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}) => ("image/png", "image"),
            ".jpg" or ".jpeg" when Starts(new byte[] {255, 216, 255}) => ("image/jpeg", "image"),
            ".gif" when Starts("GIF87a"u8.ToArray()) || Starts("GIF89a"u8.ToArray()) => ("image/gif", "image"),
            ".webp" when bytes.Length >= 12 && Starts("RIFF"u8.ToArray()) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) => ("image/webp", "image"),
            ".doc" when Starts(new byte[] {208, 207, 17, 224, 161, 177, 26, 225}) => ("application/msword", "word"),
            ".docx" when IsDocx(bytes) => ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "word"),
            _ => null
        };
    }
    private static bool IsDocx(byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            return archive.GetEntry("[Content_Types].xml") is not null
                && archive.GetEntry("word/document.xml") is not null
                && !archive.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
    }
}
