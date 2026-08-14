using CSharpFunctionalExtensions;
using Deidentify;
using Deidentify.WebConsumer;

var builder = WebApplication.CreateBuilder(args);
var localNer = LocalNerRecognizer.Create();
if (localNer.IsFailure)
{
    throw new InvalidOperationException($"{localNer.Error.Code}: {localNer.Error.SafeMessage}");
}

builder.Services.AddSingleton(new SensitiveDataAnalyzer([localNer.Value]));
builder.Services.AddSingleton<SensitiveDataSanitizer>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    status = "ready"
}));
app.MapPost("/sanitize", (SanitizeRequest request, SensitiveDataSanitizer sanitizer) =>
    sanitizer.Sanitize(request.Text).Match(
        success => Results.Ok(new
        {
            text = success.Text
        }),
        error => Results.Problem(
            title: error.Code.ToString(),
            detail: error.SafeMessage,
            statusCode: StatusCodes.Status422UnprocessableEntity)));

await app.RunAsync();
