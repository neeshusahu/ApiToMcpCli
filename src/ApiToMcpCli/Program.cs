using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

var services = new ServiceCollection();
services.AddServices();
var provider = services.BuildServiceProvider();

var rootCommand = new RootCommand("mcpgen");
var generateCommand = new Command("generate", "Generate mcp registry from open api configuration");

var inputArg = new Argument<string>("inputPath");
generateCommand.Arguments.Add(inputArg);

var outputOpt = new Option<string>("--output", "-o")
{
    Description = "Where to write the registry",
    DefaultValueFactory = _ => "registry.json"
};
generateCommand.Options.Add(outputOpt);

generateCommand.SetAction(async parseResult =>
{
    var inputPath = parseResult.GetValue(inputArg);
    var output = parseResult.GetValue(outputOpt)!;
    try
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException($"Spec not found: {inputPath}");

        var settings = provider.GetRequiredService<OpenApiReaderSettings>();

        await using var stream = File.OpenRead(inputPath);
        var (doc, diagnostic) = await OpenApiDocument.LoadAsync(stream, settings: settings);

        if (diagnostic?.Errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostic.Errors.Select(e => e.Message)));
        if (doc is null)
            throw new InvalidOperationException("No content to load");

        var response = Extractor.Extract(doc);
        await RegistryWriter.WriteAsync(response, output);

        Console.WriteLine($"Wrote {response.Operations.Count} operations to {output}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
});

rootCommand.Subcommands.Add(generateCommand);
return await rootCommand.Parse(args).InvokeAsync();

