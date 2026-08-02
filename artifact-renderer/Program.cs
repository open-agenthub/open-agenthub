using AgentHub.ArtifactRenderer;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Renderer").Get<RendererOptions>() ?? new RendererOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LibreOfficeConverter>();
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = options.MaxInputBytes + 1024 * 1024);

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapPost("/render", async (HttpRequest request, LibreOfficeConverter converter, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { code = "multipart_required" });
    var form = await request.ReadFormAsync(ct);
    if (form.Files.Count != 1) return Results.BadRequest(new { code = "one_file_required" });
    var file = form.Files[0];
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!LibreOfficeConverter.IsSupported(extension))
        return Results.BadRequest(new { code = "unsupported_file_type" });
    if (file.Length <= 0 || file.Length > options.MaxInputBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    try
    {
        await using var input = file.OpenReadStream();
        var pdf = await converter.ConvertAsync(input, extension, ct);
        return Results.File(pdf, "application/pdf", Path.GetFileNameWithoutExtension(file.FileName) + ".pdf");
    }
    catch (ConversionException exception)
    {
        return Results.UnprocessableEntity(new { code = exception.Message.Split(':')[0] });
    }
}).DisableAntiforgery();
app.Run();

public partial class Program;
