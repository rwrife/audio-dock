using System.Text.Json;
using System.Text.Json.Serialization;
using AudioDock.Core.Models;
using AudioDock.Windows;

bool includePaths = args.Contains("--include-executable-paths", StringComparer.Ordinal);
bool watch = args.Contains("--watch", StringComparer.Ordinal);
if (args.Any(argument => argument is not "--include-executable-paths" and not "--watch"))
{
    Console.Error.WriteLine("Usage: AudioDock.Diagnostics [--watch] [--include-executable-paths]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
using var inventory = new WindowsAudioInventory();
var options = new AudioInventoryOptions(includePaths);
try
{
    if (watch)
    {
        await foreach (AudioSnapshot snapshot in inventory.ObserveAsync(options, cancellation.Token))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, jsonOptions));
        }
    }
    else
    {
        AudioSnapshot snapshot = await inventory.CaptureAsync(options, cancellation.Token);
        Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, jsonOptions));
    }

    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
