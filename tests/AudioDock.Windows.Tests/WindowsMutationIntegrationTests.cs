using AudioDock.Core.Models;
using Xunit.Abstractions;

namespace AudioDock.Windows.Tests;

public sealed class WindowsMutationIntegrationTests(ITestOutputHelper output)
{
    [WindowsMutationFact]
    public async Task OptInEndpointVolumeWriteAlwaysRestoresHostState()
    {
        string endpointId = Environment.GetEnvironmentVariable("AUDIO_DOCK_MUTATION_ENDPOINT_ID")!;

        using var adapter = new WindowsAudioInventory();
        AudioSnapshot before = await adapter.CaptureAsync();
        EndpointDescriptor? endpoint = before.Endpoints.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.StableId, endpointId));
        if (endpoint is null)
        {
            output.WriteLine("Mutation was not attempted: the approved endpoint was not present.");
            throw new InvalidOperationException(
                "The explicitly approved endpoint was not found; no audio state was changed.");
        }

        if (endpoint.State != EndpointState.Active ||
            !endpoint.Capabilities.CanSetVolume ||
            endpoint.Volume is null)
        {
            output.WriteLine(
                "Mutation was not attempted: the approved endpoint is not an active, volume-controllable endpoint.");
            throw new InvalidOperationException(
                "The explicitly approved endpoint is not safe to mutate; no audio state was changed.");
        }

        VolumeLevel original = endpoint.Volume.Value;
        var testValue = new VolumeLevel(original.Value >= 0.95
            ? original.Value - 0.05
            : original.Value + 0.05);
        Exception? testFailure = null;
        string? restorationFailure = null;
        try
        {
            ControlWriteResult write = await adapter.WriteAsync(
                new(ChangeKind.EndpointVolume, endpoint.StableId, Volume: testValue));
            Assert.True(write.Succeeded, write.Detail);
            AudioSnapshot observed = await adapter.CaptureAsync();
            VolumeLevel? actual = observed.Endpoints.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.StableId, endpoint.StableId))?.Volume;
            Assert.NotNull(actual);
            Assert.InRange(Math.Abs(actual.Value.Value - testValue.Value), 0, 0.005);
            output.WriteLine("Mutation verified for the explicitly approved endpoint; the adapter opened no audio stream.");
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            ControlWriteResult restore = await adapter.WriteAsync(
                new(ChangeKind.EndpointVolume, endpoint.StableId, Volume: original));
            AudioSnapshot restored = await adapter.CaptureAsync();
            VolumeLevel? actual = restored.Endpoints.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.StableId, endpoint.StableId))?.Volume;
            bool restorationVerified = restore.Succeeded && actual is not null &&
                Math.Abs(actual.Value.Value - original.Value) <= 0.005;
            output.WriteLine($"Restoration result: write={restore.Succeeded}, verified={restorationVerified}.");
            if (!restorationVerified)
            {
                restorationFailure = $"Host audio restoration failed: {restore.Detail}";
            }
        }

        if (restorationFailure is not null)
        {
            throw new InvalidOperationException(restorationFailure, testFailure);
        }

        if (testFailure is not null)
        {
            throw testFailure;
        }
    }
}

internal sealed class WindowsMutationFactAttribute : FactAttribute
{
    public WindowsMutationFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows Core Audio mutation requires a supported Windows host.";
        }
        else if (!StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("AUDIO_DOCK_MUTATION_TEST"),
            "1"))
        {
            Skip = "Set AUDIO_DOCK_MUTATION_TEST=1 to opt in to host audio mutation.";
        }
        else if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("AUDIO_DOCK_MUTATION_ENDPOINT_ID")))
        {
            Skip = "Set AUDIO_DOCK_MUTATION_ENDPOINT_ID to an explicitly approved active endpoint ID.";
        }
    }
}
