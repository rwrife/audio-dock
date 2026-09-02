using System.Collections.ObjectModel;
using AudioDock.Core.Execution;
using AudioDock.Core.Models;
using AudioDock.Core.Workflow;

namespace AudioDock.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISceneWorkflow workflow;
    private AudioScene? selectedScene;
    private ScenePreview? preview;
    private ActivityRecord? selectedActivity;
    private string editName = "New scene";
    private string status = "Ready. Select or create a scene, then preview before applying.";
    private bool isBusy;
    private bool reviewConfirmed;
    private Guid? undoId;
    private CancellationTokenSource? applyCancellation;
    private Task activeOperationCompletion = Task.CompletedTask;
    private string manualEndpointId = string.Empty;
    private string manualEndpointVolume = "0.5";
    private string manualApplication = string.Empty;
    private string manualApplicationVolume = "0.5";
    private AudioDirection manualDirection;
    private AudioRole manualRole = AudioRole.Multimedia;
    private bool? manualEndpointMute;
    private bool? manualApplicationMute;
    private int selectedRoleIndex = -1;
    private int selectedEndpointIndex = -1;
    private int selectedApplicationIndex = -1;
    private readonly Array directions = Enum.GetValues<AudioDirection>();
    private readonly Array roles = Enum.GetValues<AudioRole>();

    public MainViewModel(ISceneWorkflow workflow)
    {
        this.workflow = workflow;
        NewCommand = new(() => _ = CreateBlankAsync(), () => !IsBusy);
        CaptureCommand = new(() => _ = CaptureAsync(), () => !IsBusy);
        RenameCommand = new(() => _ = RenameAsync(), () => SelectedScene is not null && !IsBusy);
        DuplicateCommand = new(() => _ = DuplicateAsync(), () => SelectedScene is not null && !IsBusy);
        DeleteCommand = new(() => _ = DeleteAsync(), () => SelectedScene is not null && !IsBusy);
        PreviewCommand = new(() => _ = PreviewAsync(), () => SelectedScene is not null && !IsBusy);
        ApplyCommand = new(() => _ = ApplyAsync(), () => Preview is not null && ReviewConfirmed && !Preview.RequiresResolution && !IsBusy);
        CancelCommand = new(() => { applyCancellation?.Cancel(); Status = "Cancellation requested; the current native write/read-back will finish before stopping."; }, () => IsBusy && applyCancellation is not null);
        UndoCommand = new(() => _ = UndoAsync(), () => undoId is not null && !IsBusy);
        ClearUndoCommand = new(() => _ = ClearUndoAsync(), () => undoId is not null && !IsBusy);
        AddEndpointRuleCommand = new(() => _ = AddEndpointRuleAsync(), () => SelectedScene is not null && !IsBusy);
        AddApplicationRuleCommand = new(() => _ = AddApplicationRuleAsync(), () => SelectedScene is not null && !IsBusy);
        AddRoleRuleCommand = new(() => _ = UpsertRoleAsync(-1), () => SelectedScene is not null && !IsBusy);
        ChangeRoleRuleCommand = new(() => _ = UpsertRoleAsync(SelectedRoleIndex), () => SelectedRoleIndex >= 0 && !IsBusy);
        RemoveRoleRuleCommand = new(() => _ = RemoveRuleAsync(RuleKind.Role), () => SelectedRoleIndex >= 0 && !IsBusy);
        ChangeEndpointRuleCommand = new(() => _ = UpsertEndpointAsync(SelectedEndpointIndex), () => SelectedEndpointIndex >= 0 && !IsBusy);
        RemoveEndpointRuleCommand = new(() => _ = RemoveRuleAsync(RuleKind.Endpoint), () => SelectedEndpointIndex >= 0 && !IsBusy);
        ChangeApplicationRuleCommand = new(() => _ = UpsertApplicationAsync(SelectedApplicationIndex), () => SelectedApplicationIndex >= 0 && !IsBusy);
        RemoveApplicationRuleCommand = new(() => _ = RemoveRuleAsync(RuleKind.Application), () => SelectedApplicationIndex >= 0 && !IsBusy);
    }

    public ObservableCollection<AudioScene> Scenes { get; } = [];
    public ObservableCollection<ActivityRecord> Activity { get; } = [];
    public DelegateCommand NewCommand { get; }
    public DelegateCommand CaptureCommand { get; }
    public DelegateCommand RenameCommand { get; }
    public DelegateCommand DuplicateCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand PreviewCommand { get; }
    public DelegateCommand ApplyCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand UndoCommand { get; }
    public DelegateCommand ClearUndoCommand { get; }
    public DelegateCommand AddEndpointRuleCommand { get; }
    public DelegateCommand AddApplicationRuleCommand { get; }
    public DelegateCommand AddRoleRuleCommand { get; }
    public DelegateCommand ChangeRoleRuleCommand { get; }
    public DelegateCommand RemoveRoleRuleCommand { get; }
    public DelegateCommand ChangeEndpointRuleCommand { get; }
    public DelegateCommand RemoveEndpointRuleCommand { get; }
    public DelegateCommand ChangeApplicationRuleCommand { get; }
    public DelegateCommand RemoveApplicationRuleCommand { get; }

    public AudioScene? SelectedScene
    {
        get => selectedScene;
        set
        {
            if (!Set(ref selectedScene, value)) return;
            EditName = value?.Name ?? "New scene";
            Preview = null;
            ReviewConfirmed = false;
            SelectedRoleIndex = SelectedEndpointIndex = SelectedApplicationIndex = -1;
            RaiseRuleLists();
            RefreshCommands();
        }
    }

    public ScenePreview? Preview { get => preview; private set { if (Set(ref preview, value)) { Raise(nameof(PlanChanges)); Raise(nameof(MatchReviews)); RefreshCommands(); } } }
    public IEnumerable<PlannedChange> PlanChanges => Preview?.Plan.Changes ?? [];
    public IEnumerable<MatchReview> MatchReviews => Preview?.Matches ?? [];
    public ActivityRecord? SelectedActivity { get => selectedActivity; set { if (Set(ref selectedActivity, value)) Raise(nameof(ActivityOperations)); } }
    public IEnumerable<OperationResult> ActivityOperations => SelectedActivity?.ApplyResult?.Operations ?? [];
    public string EditName { get => editName; set => Set(ref editName, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) RefreshCommands(); } }
    public bool ReviewConfirmed { get => reviewConfirmed; set { if (Set(ref reviewConfirmed, value)) ApplyCommand.Refresh(); } }
    public string ManualEndpointId { get => manualEndpointId; set => Set(ref manualEndpointId, value); }
    public string ManualEndpointVolume { get => manualEndpointVolume; set => Set(ref manualEndpointVolume, value); }
    public string ManualApplication { get => manualApplication; set => Set(ref manualApplication, value); }
    public string ManualApplicationVolume { get => manualApplicationVolume; set => Set(ref manualApplicationVolume, value); }
    public AudioDirection ManualDirection { get => manualDirection; set => Set(ref manualDirection, value); }
    public AudioRole ManualRole { get => manualRole; set => Set(ref manualRole, value); }
    public bool? ManualEndpointMute { get => manualEndpointMute; set => Set(ref manualEndpointMute, value); }
    public bool? ManualApplicationMute { get => manualApplicationMute; set => Set(ref manualApplicationMute, value); }
    public Array Directions => directions;
    public Array Roles => roles;
    public IReadOnlyList<RoleTarget> RoleRules => SelectedScene?.RoleTargets ?? [];
    public IReadOnlyList<EndpointRule> EndpointRules => SelectedScene?.EndpointRules ?? [];
    public IReadOnlyList<ApplicationRule> ApplicationRules => SelectedScene?.ApplicationRules ?? [];
    public int SelectedRoleIndex { get => selectedRoleIndex; set { if (Set(ref selectedRoleIndex, value)) { LoadRole(value); RefreshCommands(); } } }
    public int SelectedEndpointIndex { get => selectedEndpointIndex; set { if (Set(ref selectedEndpointIndex, value)) { LoadEndpoint(value); RefreshCommands(); } } }
    public int SelectedApplicationIndex { get => selectedApplicationIndex; set { if (Set(ref selectedApplicationIndex, value)) { LoadApplication(value); RefreshCommands(); } } }

    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            Replace(Scenes, await workflow.LoadScenesAsync());
            Replace(Activity, await workflow.LoadActivityAsync());
            SelectedScene = Scenes.FirstOrDefault();
            SelectedActivity = Activity.FirstOrDefault();
            Status = Scenes.Count == 0 ? "No scenes yet. Create one manually or capture current metadata and volume state." : $"Loaded {Scenes.Count} scene(s).";
        });
    }

    public async Task ApplyNamedAsync(AudioScene scene)
    {
        if (IsBusy) return;
        SelectedScene = scene;
        await PreviewAsync();
        if (Preview is { RequiresResolution: false }) { ReviewConfirmed = true; await ApplyAsync(); }
    }
    public async Task UndoLatestAsync() => await UndoAsync();

    private async Task ClearUndoAsync()
    {
        await workflow.ClearUndoAsync();
        undoId = null;
        UndoCommand.Refresh();
        ClearUndoCommand.Refresh();
        Status = "The in-memory undo snapshot was cleared. Audio state was not changed.";
    }

    public async Task CancelActiveOperationAndWaitAsync()
    {
        CancellationTokenSource? cancellation = applyCancellation;
        Task completion = activeOperationCompletion;
        cancellation?.Cancel();
        if (completion.IsCompleted) return;
        Status = cancellation is null
            ? "Waiting for the current operation to finish before closing."
            : "Cancellation requested; waiting for the current transaction to reach a safe boundary.";
        await completion;
    }

    private async Task CreateBlankAsync()
    {
        var scene = new AudioScene(AudioScene.CurrentSchemaVersion, Guid.NewGuid(), ValidName());
        Scenes.Add(scene); SelectedScene = scene; await SaveAsync("Created a blank scene for manual editing.");
    }

    private async Task CaptureAsync() => await RunAsync(async () =>
    {
        AudioScene scene = await workflow.CaptureCurrentAsync(ValidName());
        Scenes.Add(scene); SelectedScene = scene; await workflow.SaveAsync(Scenes);
        Status = "Captured endpoint/session metadata and current control values. No audio samples were accessed.";
    });

    private async Task RenameAsync()
    {
        if (SelectedScene is null) return;
        ReplaceSelected(Copy(SelectedScene, SelectedScene.Id, ValidName()));
        await SaveAsync("Scene renamed.");
    }

    private async Task DuplicateAsync()
    {
        if (SelectedScene is null) return;
        AudioScene copy = Copy(SelectedScene, Guid.NewGuid(), $"{SelectedScene.Name} copy");
        Scenes.Add(copy); SelectedScene = copy; await SaveAsync("Scene duplicated.");
    }

    private async Task DeleteAsync()
    {
        if (SelectedScene is null) return;
        int index = Scenes.IndexOf(SelectedScene); Scenes.Remove(SelectedScene);
        SelectedScene = Scenes.ElementAtOrDefault(Math.Min(index, Scenes.Count - 1));
        await SaveAsync("Scene deleted from local storage.");
    }

    private async Task AddEndpointRuleAsync()
    {
        await UpsertEndpointAsync(-1);
    }

    private async Task AddApplicationRuleAsync()
    {
        await UpsertApplicationAsync(-1);
    }

    private async Task PreviewAsync() => await RunAsync(async () =>
    {
        if (SelectedScene is null) return;
        Preview = await workflow.PreviewAsync(SelectedScene);
        ReviewConfirmed = false;
        Status = Preview.RequiresResolution
            ? "Preview blocked: one or more targets are missing or ambiguous. Edit the scene before apply."
            : $"Preview ready: {Preview.Plan.Changes.Count} before/after operation(s). Review and confirm to enable Apply.";
    });

    private async Task ApplyAsync()
    {
        if (SelectedScene is null || Preview is null || !ReviewConfirmed || Preview.RequiresResolution) return;
        using var operationCancellation = new CancellationTokenSource();
        applyCancellation = operationCancellation;
        try
        {
            await RunAsync(async () =>
            {
                AudioScene appliedScene = Preview.ReviewedScene;
                ScenePreview appliedPreview = Preview;
                ApplyResult result = await workflow.ApplyAsync(appliedPreview, operationCancellation.Token);
                undoId = result.Rollback.SnapshotId;
                ClearUndoCommand.Refresh();
                string summary = result.ActivityPersistenceFailure is null
                    ? $"Apply {result.State}: review operation details below."
                    : $"Apply {result.State}: in-memory only; activity history save failed.";
                ActivityRecord record = new(Guid.NewGuid(), appliedScene.Id, appliedScene.Name, result.CompletedAt, summary, result);
                Activity.Insert(0, record); SelectedActivity = record;
                Status = result.ActivityPersistenceFailure is null
                    ? $"Apply {result.State}. {result.Rollback.Detail}"
                    : $"Apply {result.State}. {result.Rollback.Detail} Durability warning: {result.ActivityPersistenceFailure} Undo remains available for this session.";
                Preview = null; ReviewConfirmed = false;
            });
        }
        finally
        {
            if (ReferenceEquals(applyCancellation, operationCancellation)) applyCancellation = null;
            RefreshCommands();
        }
    }

    private async Task UndoAsync()
    {
        if (undoId is not Guid id) return;
        await RunAsync(async () =>
        {
            RollbackFact result = await workflow.UndoAsync(id, SelectedScene?.Name ?? "Latest apply");
            undoId = null;
            Status = result.ActivityPersistenceFailure is null
                ? $"Undo {result.State}: {result.Detail}"
                : $"Undo {result.State}: {result.Detail} Durability warning: {result.ActivityPersistenceFailure}";
            UndoCommand.Refresh();
            ClearUndoCommand.Refresh();
        });
    }

    private async Task SaveAsync(string message) { await workflow.SaveAsync(Scenes); Status = message; }
    private string ValidName() => string.IsNullOrWhiteSpace(EditName) ? "Untitled scene" : EditName.Trim();
    private static AudioScene Copy(AudioScene source, Guid id, string name) => new(source.SchemaVersion, id, name, source.RoleTargets, source.EndpointRules, source.ApplicationRules);
    private void ReplaceSelected(AudioScene replacement)
    {
        int sceneIndex = Scenes.IndexOf(SelectedScene!);
        int roleIndex = SelectedRoleIndex;
        int endpointIndex = SelectedEndpointIndex;
        int applicationIndex = SelectedApplicationIndex;
        Scenes[sceneIndex] = replacement;
        SelectedScene = replacement;
        SelectedRoleIndex = roleIndex < replacement.RoleTargets.Count ? roleIndex : -1;
        SelectedEndpointIndex = endpointIndex < replacement.EndpointRules.Count ? endpointIndex : -1;
        SelectedApplicationIndex = applicationIndex < replacement.ApplicationRules.Count ? applicationIndex : -1;
    }
    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeOperationCompletion = completion.Task;
        IsBusy = true;
        try { await operation(); }
        catch (OperationCanceledException) { Status = "Canceled at a safe operation boundary."; }
        catch (StaleScenePreviewException exception) { Preview = null; ReviewConfirmed = false; Status = $"Preview expired: {exception.Message}"; }
        catch (Exception exception) { Status = $"Error: {exception.Message}"; }
        finally
        {
            IsBusy = false;
            completion.SetResult();
            if (ReferenceEquals(activeOperationCompletion, completion.Task)) activeOperationCompletion = Task.CompletedTask;
        }
    }
    private async Task UpsertRoleAsync(int index) => await EditRulesAsync(() => SceneRuleEditor.UpsertRole(SelectedScene!, index, ManualRole, ManualDirection, ManualEndpointId), index < 0 ? "Role rule added." : "Role rule changed.");
    private async Task UpsertEndpointAsync(int index) => await EditRulesAsync(() => SceneRuleEditor.UpsertEndpoint(SelectedScene!, index, ManualDirection, ManualEndpointId, ManualEndpointVolume, ManualEndpointMute), index < 0 ? "Endpoint rule added." : "Endpoint rule changed.");
    private async Task UpsertApplicationAsync(int index) => await EditRulesAsync(() => SceneRuleEditor.UpsertApplication(SelectedScene!, index, ManualApplication, ManualApplicationVolume, ManualApplicationMute), index < 0 ? "Application rule added." : "Application rule changed.");
    private async Task RemoveRuleAsync(RuleKind kind) => await EditRulesAsync(() => kind switch { RuleKind.Role => SceneRuleEditor.RemoveRole(SelectedScene!, SelectedRoleIndex), RuleKind.Endpoint => SceneRuleEditor.RemoveEndpoint(SelectedScene!, SelectedEndpointIndex), _ => SceneRuleEditor.RemoveApplication(SelectedScene!, SelectedApplicationIndex) }, $"{kind} rule removed.");
    private async Task EditRulesAsync(Func<AudioScene> edit, string message) { if (SelectedScene is null || IsBusy) return; try { ReplaceSelected(edit()); await SaveAsync(message); } catch (ArgumentException exception) { Status = exception.Message; } }
    private void LoadRole(int index) { if (index < 0 || SelectedScene is null) return; RoleTarget rule = SelectedScene.RoleTargets[index]; ManualRole = rule.Role; ManualDirection = rule.Endpoint.Direction; ManualEndpointId = rule.Endpoint.ExactId ?? string.Empty; }
    private void LoadEndpoint(int index) { if (index < 0 || SelectedScene is null) return; EndpointRule rule = SelectedScene.EndpointRules[index]; ManualDirection = rule.Match.Direction; ManualEndpointId = rule.Match.ExactId ?? string.Empty; ManualEndpointVolume = rule.Volume?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; ManualEndpointMute = rule.IsMuted; }
    private void LoadApplication(int index) { if (index < 0 || SelectedScene is null) return; ApplicationRule rule = SelectedScene.ApplicationRules[index]; ManualApplication = rule.Match.ProcessName ?? string.Empty; ManualApplicationVolume = rule.Volume?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; ManualApplicationMute = rule.IsMuted; }
    private void RaiseRuleLists() { Raise(nameof(RoleRules)); Raise(nameof(EndpointRules)); Raise(nameof(ApplicationRules)); }
    private void RefreshCommands() { NewCommand.Refresh(); CaptureCommand.Refresh(); RenameCommand.Refresh(); DuplicateCommand.Refresh(); DeleteCommand.Refresh(); PreviewCommand.Refresh(); ApplyCommand.Refresh(); CancelCommand.Refresh(); UndoCommand.Refresh(); ClearUndoCommand.Refresh(); AddEndpointRuleCommand.Refresh(); AddApplicationRuleCommand.Refresh(); AddRoleRuleCommand.Refresh(); ChangeRoleRuleCommand.Refresh(); RemoveRoleRuleCommand.Refresh(); ChangeEndpointRuleCommand.Refresh(); RemoveEndpointRuleCommand.Refresh(); ChangeApplicationRuleCommand.Refresh(); RemoveApplicationRuleCommand.Refresh(); }
    private enum RuleKind { Role, Endpoint, Application }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (T value in values) target.Add(value); }

    public void Dispose()
    {
        applyCancellation?.Dispose();
        applyCancellation = null;
        GC.SuppressFinalize(this);
    }
}
