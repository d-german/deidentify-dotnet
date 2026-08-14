using Deidentify;

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync("Usage: Deidentify.Cli <text> | --file <input> <output>");
    return 2;
}

var localNer = LocalNerRecognizer.Create();
if (localNer.IsFailure)
{
    await Console.Error.WriteLineAsync($"{localNer.Error.Code}: {localNer.Error.SafeMessage}");
    return 1;
}

var sanitizer = new SensitiveDataSanitizer(new SensitiveDataAnalyzer([localNer.Value]));

if (args is ["--file", var inputPath, var outputPath])
{
    using var input = File.OpenText(inputPath);
    using var output = File.CreateText(outputPath);
    var result = await new StreamingSanitizer(sanitizer).SanitizeAsync(input, output);
    if (result.IsFailure)
    {
        await Console.Error.WriteLineAsync($"{result.Error.Code}: {result.Error.SafeMessage}");
        return 1;
    }

    await Console.Error.WriteLineAsync($"Sanitized {result.Value.LinesProcessed} lines.");
    return 0;
}

var sanitized = sanitizer.Sanitize(string.Join(' ', args));
if (sanitized.IsFailure)
{
    await Console.Error.WriteLineAsync($"{sanitized.Error.Code}: {sanitized.Error.SafeMessage}");
    return 1;
}

await Console.Out.WriteLineAsync(sanitized.Value.Text);
return 0;
