using AudioDock.App.ViewModels;
using AudioDock.Core.Models;
using AudioDock.Core.Workflow;

namespace AudioDock.Core.Tests.Workflow;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task SceneCommandsCreateRenameDuplicateAndDeleteThroughWorkflowBoundary()
    {
        var workflow = new FakeWorkflow();
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.EditName = "Manual";
        viewModel.NewCommand.Execute(null);
        Assert.Equal("Manual", Assert.Single(viewModel.Scenes).Name);
        viewModel.EditName = "Renamed";
        viewModel.RenameCommand.Execute(null);
        Assert.Equal("Renamed", Assert.Single(viewModel.Scenes).Name);
        viewModel.DuplicateCommand.Execute(null);
        Assert.Equal(2, viewModel.Scenes.Count);
        viewModel.DeleteCommand.Execute(null);
        Assert.Single(viewModel.Scenes);
        Assert.NotEmpty(workflow.Saves);
    }

    [Fact]
    public async Task ApplyStaysDisabledUntilPreviewIsResolvedAndExplicitlyReviewed()
    {
        var scene = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk");
        var workflow = new FakeWorkflow { Initial = [scene] };
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        viewModel.PreviewCommand.Execute(null);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        viewModel.ReviewConfirmed = true;
        Assert.True(viewModel.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManualRulesCanBeAddedChangedAndRemovedWithBoundedValidation()
    {
        var scene = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk");
        var viewModel = new MainViewModel(new FakeWorkflow { Initial = [scene] });
        await viewModel.InitializeAsync();
        viewModel.ManualDirection = AudioDirection.Capture;
        viewModel.ManualRole = AudioRole.Communications;
        viewModel.ManualEndpointId = "microphone";
        viewModel.AddRoleRuleCommand.Execute(null);
        Assert.Equal(AudioDirection.Capture, Assert.Single(viewModel.RoleRules).Endpoint.Direction);
        viewModel.ManualEndpointVolume = "0.7";
        viewModel.ManualEndpointMute = true;
        viewModel.AddEndpointRuleCommand.Execute(null);
        viewModel.SelectedEndpointIndex = 0;
        viewModel.ManualEndpointVolume = "1.2";
        viewModel.ChangeEndpointRuleCommand.Execute(null);
        Assert.Contains("0 through 1", viewModel.Status);
        viewModel.ManualEndpointVolume = "0.4";
        viewModel.ManualEndpointMute = false;
        viewModel.ChangeEndpointRuleCommand.Execute(null);
        Assert.Equal(new VolumeLevel(0.4), Assert.Single(viewModel.EndpointRules).Volume);
        viewModel.RemoveEndpointRuleCommand.Execute(null);
        Assert.Empty(viewModel.EndpointRules);

        viewModel.ManualApplication = "player";
        viewModel.ManualApplicationVolume = "";
        viewModel.ManualApplicationMute = true;
        viewModel.AddApplicationRuleCommand.Execute(null);
        Assert.True(Assert.Single(viewModel.ApplicationRules).IsMuted);
        viewModel.SelectedApplicationIndex = 0;
        viewModel.RemoveApplicationRuleCommand.Execute(null);
        Assert.Empty(viewModel.ApplicationRules);
    }

    [Fact]
    public async Task BusyApplyDisablesSceneMutationAndAttributesResultToReviewedScene()
    {
        var first = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "First");
        var second = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Second");
        var workflow = new FakeWorkflow { Initial = [first, second], ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();
        viewModel.PreviewCommand.Execute(null);
        viewModel.ReviewConfirmed = true;
        viewModel.ApplyCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsBusy);

        Assert.False(viewModel.NewCommand.CanExecute(null));
        Assert.False(viewModel.CaptureCommand.CanExecute(null));
        viewModel.SelectedScene = second;
        Assert.Equal(second.Id, viewModel.SelectedScene!.Id);
        workflow.ApplyGate.SetResult();
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.Equal(first.Id, Assert.Single(viewModel.Activity).SceneId);
    }

    [Fact]
    public async Task ReentrantNamedApplyLeavesActiveSelectionAndCancellationOwnedByOriginalApply()
    {
        var first = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "First");
        var second = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Second");
        var workflow = new FakeWorkflow { Initial = [first, second], ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();

        Task original = viewModel.ApplyNamedAsync(first);
        await WaitUntilAsync(() => viewModel.IsBusy && workflow.ApplyToken.CanBeCanceled);
        await viewModel.ApplyNamedAsync(second);

        Assert.Equal(first.Id, viewModel.SelectedScene!.Id);
        viewModel.CancelCommand.Execute(null);
        await original;
        Assert.True(workflow.ApplyToken.IsCancellationRequested);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ShutdownCancellationWaitsForApplySafeBoundaryCompletion()
    {
        var scene = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk");
        var workflow = new FakeWorkflow
        {
            Initial = [scene],
            ApplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
            WaitForBoundaryAfterCancellation = true,
        };
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();

        Task apply = viewModel.ApplyNamedAsync(scene);
        await WaitUntilAsync(() => workflow.ApplyToken.CanBeCanceled);
        Task shutdown = viewModel.CancelActiveOperationAndWaitAsync();

        Assert.True(workflow.ApplyToken.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        workflow.ApplyGate.SetResult();
        await shutdown;
        await apply;
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task UndoPersistenceWarningClearsConsumedUndoAndIsReported()
    {
        var scene = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), "Desk");
        var workflow = new FakeWorkflow
        {
            Initial = [scene],
            ApplyUndoId = Guid.NewGuid(),
            UndoPersistenceFailure = "Activity history could not be saved: disk full",
        };
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();
        await viewModel.ApplyNamedAsync(scene);
        Assert.True(viewModel.UndoCommand.CanExecute(null));

        await viewModel.UndoLatestAsync();

        Assert.False(viewModel.UndoCommand.CanExecute(null));
        Assert.Contains("Durability warning", viewModel.Status);
        Assert.Contains("disk full", viewModel.Status);
    }

    [Fact]
    public async Task ClearUndoIsEnabledOnlyWhileUndoIsAvailableAndNotBusy()
    {
        var scene = new AudioScene(1, Guid.NewGuid(), "Desk");
        var workflow = new FakeWorkflow { Initial = [scene], ApplyUndoId = Guid.NewGuid() };
        var viewModel = new MainViewModel(workflow);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.ClearUndoCommand.CanExecute(null));

        await viewModel.ApplyNamedAsync(scene);
        Assert.True(viewModel.ClearUndoCommand.CanExecute(null));
        viewModel.ClearUndoCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.ClearUndoCommand.CanExecute(null));
    }

    [Fact]
    public void TrayStateUsesPauseResumeCheckedAndTextOnlyStatusCues()
    {
        var paused = new TrayHotkeyState(true, true, false, null);
        Assert.Equal("Resume hotkeys", paused.PauseText);
        Assert.True(paused.PauseChecked);
        Assert.Contains("paused", paused.StatusText, StringComparison.OrdinalIgnoreCase);
        var conflict = new TrayHotkeyState(true, false, false, "Control+Alt+D is unavailable");
        Assert.Contains("unavailable", conflict.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int count = 0; count < 100 && !condition(); count++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeWorkflow : ISceneWorkflow
    {
        public IReadOnlyList<AudioScene> Initial { get; init; } = [];
        public List<IReadOnlyCollection<AudioScene>> Saves { get; } = [];
        public TaskCompletionSource? ApplyGate { get; init; }
        public CancellationToken ApplyToken { get; private set; }
        public Guid? ApplyUndoId { get; init; }
        public string? UndoPersistenceFailure { get; init; }
        public bool WaitForBoundaryAfterCancellation { get; init; }
        public ValueTask<IReadOnlyList<AudioScene>> LoadScenesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Initial);
        public ValueTask<IReadOnlyList<ActivityRecord>> LoadActivityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ActivityRecord>>([]);
        public ValueTask<AudioScene> CaptureCurrentAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), name));
        public ValueTask SaveAsync(IReadOnlyCollection<AudioScene> values, CancellationToken cancellationToken = default) { Saves.Add([.. values]); return ValueTask.CompletedTask; }
        public ValueTask<ScenePreview> PreviewAsync(AudioScene scene, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ScenePreview(scene, new(scene.Id, []), [], new(true, true, true, null)));
        public async ValueTask<ApplyResult> ApplyAsync(ScenePreview preview, CancellationToken cancellationToken = default)
        {
            ApplyToken = cancellationToken;
            if (ApplyGate is not null)
            {
                try { await ApplyGate.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (WaitForBoundaryAfterCancellation) { await ApplyGate.Task; throw; }
            }
            return new(preview.Plan.SceneId, ApplyOverallState.Applied, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [],
                ApplyUndoId is Guid id
                    ? new(RollbackState.Available, id, "Undo available.")
                    : new(RollbackState.NotRequired, null, "No changes."));
        }
        public ValueTask<RollbackFact> UndoAsync(Guid snapshotId, string sceneName) => ValueTask.FromResult(
            new RollbackFact(RollbackState.Succeeded, snapshotId, "Undone.", UndoPersistenceFailure));
    }
}
