using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;

namespace BimScriptControlPanel;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ScriptEntry> _scripts = new();
    private readonly ObservableCollection<WorkflowPreviewStep> _workflowPreview = new();
    private readonly List<WorkflowEdge> _workflowEdges = new();
    private readonly ObservableCollection<WorkflowPreset> _workflowPresets = new();

    private readonly string _catalogFilePath;
    private readonly object _processLock = new();
    private readonly HashSet<Process> _runningProcesses = new();

    private bool _isExecuting;
    private bool _stopRequested;

    private string _sortProperty = "Name";
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public MainWindow()
    {
        InitializeComponent();

        _catalogFilePath = BuildCatalogPath();
        ScriptListView.ItemsSource = _scripts;
        WorkflowPreviewListBox.ItemsSource = _workflowPreview;
        WorkflowPresetComboBox.ItemsSource = _workflowPresets;

        LoadCatalog();
        ApplySortView("Name", ListSortDirection.Ascending);

        if (_scripts.Count > 0)
        {
            ScriptListView.SelectedIndex = 0;
        }

        UpdateSelectionDetails();
        UpdateUiState();
    }

    private static string BuildCatalogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(localAppData, "BimScriptControlPanel");
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "scripts_catalog.json");
    }

    private void AddScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickBatFile("Select BAT File");
        if (path is null)
        {
            return;
        }

        var normalized = Path.GetFullPath(path);
        var existing = _scripts.FirstOrDefault(x => string.Equals(x.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ScriptListView.SelectedItem = existing;
            return;
        }

        var entry = new ScriptEntry
        {
            Name = BuildDisplayName(normalized),
            Path = normalized,
            DateModified = GetDateModified(normalized),
        };

        _scripts.Add(entry);
        ApplySortView(_sortProperty, _sortDirection, entry.Path);

        SaveCatalog();
        UpdateUiState();
    }

    private void RemoveScriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || ScriptListView.SelectedItem is not ScriptEntry selected)
        {
            return;
        }

        var index = ScriptListView.SelectedIndex;
        _scripts.Remove(selected);
        RemoveWorkflowEdgesByPath(selected.Path);
        RebuildWorkflowPreview();

        if (_scripts.Count > 0)
        {
            ScriptListView.SelectedIndex = Math.Min(index, _scripts.Count - 1);
        }

        SaveCatalog();
        UpdateUiState();
    }

    private void ChangePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || ScriptListView.SelectedItem is not ScriptEntry selected)
        {
            return;
        }

        var path = PickBatFile("Replace BAT File");
        if (path is null)
        {
            return;
        }

        var oldPath = selected.Path;
        var normalized = Path.GetFullPath(path);

        selected.Path = normalized;
        selected.Name = BuildDisplayName(normalized);
        selected.DateModified = GetDateModified(normalized);

        UpdateWorkflowEdgesForPath(oldPath, normalized);
        NormalizeWorkflowEdges();

        ScriptListView.Items.Refresh();
        ApplySortView(_sortProperty, _sortDirection, selected.Path);
        RebuildWorkflowPreview();

        SaveCatalog();
        UpdateUiState();
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || ScriptListView.SelectedItem is not ScriptEntry selected)
        {
            return;
        }

        if (!File.Exists(selected.Path))
        {
            MessageBox.Show($"BAT file not found:\n{selected.Path}", "Script not found", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _stopRequested = false;
        SetExecutionState(true, "Status: Running Script");

        AppendLog($">>> Starting script: {selected.Name}");
        AppendLog($">>> Path: {selected.Path}");

        int exitCode = -1;
        try
        {
            exitCode = await RunProcessAsync(selected.Path, selected.Name, $"[{selected.Name}] ");
            AppendLog($">>> Finished with exit code: {exitCode}");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            LastRunText.Text = $"Last run exit code: {exitCode}";
            SetExecutionState(false, "Status: Idle");
        }
    }

    private async void RunWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting)
        {
            return;
        }

        if (_workflowEdges.Count == 0)
        {
            MessageBox.Show("Define links in Node Builder first.", "No workflow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var graph = BuildGraphFromEdges();
        if (graph.Nodes.Count == 0)
        {
            MessageBox.Show("No valid workflow nodes found.", "No workflow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!IsAcyclic(graph))
        {
            MessageBox.Show("Workflow contains a cycle. Fix links in Node Builder.", "Invalid workflow", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _stopRequested = false;
        SetExecutionState(true, "Status: Running Workflow");

        var stopOnFailure = StopOnFirstFailureCheckBox.IsChecked == true;
        var maxParallel = GetMaxParallel();

        AppendLog($">>> Starting workflow with {graph.Nodes.Count} node(s), max parallel = {maxParallel}");

        var state = graph.Nodes.ToDictionary(x => x, _ => NodeRuntimeState.Pending, StringComparer.OrdinalIgnoreCase);
        var lastExitCode = 0;

        var ready = new Queue<string>(graph.Nodes.Where(n => graph.Incoming[n].Count == 0));
        var running = new Dictionary<Task<int>, string>();

        void TryResolve(string node)
        {
            if (!state.TryGetValue(node, out var nodeState) || nodeState != NodeRuntimeState.Pending)
            {
                return;
            }

            var deps = graph.Incoming[node];
            if (deps.Any(d => state[d] is NodeRuntimeState.Pending or NodeRuntimeState.Running))
            {
                return;
            }

            if (deps.All(d => state[d] == NodeRuntimeState.Success))
            {
                ready.Enqueue(node);
                return;
            }

            state[node] = NodeRuntimeState.Skipped;
            AppendLog($">>> Skipped {GetScriptName(node)} due to failed/skipped dependency.");
            foreach (var child in graph.Outgoing[node])
            {
                TryResolve(child);
            }
        }

        try
        {
            while (ready.Count > 0 || running.Count > 0)
            {
                while (!_stopRequested && ready.Count > 0 && running.Count < maxParallel)
                {
                    var node = ready.Dequeue();
                    if (state[node] != NodeRuntimeState.Pending)
                    {
                        continue;
                    }

                    state[node] = NodeRuntimeState.Running;
                    var scriptName = GetScriptName(node);

                    if (!File.Exists(node))
                    {
                        state[node] = NodeRuntimeState.Failed;
                        lastExitCode = -1;
                        AppendLog($"ERROR: Missing file for node {scriptName}: {node}");

                        if (stopOnFailure)
                        {
                            _stopRequested = true;
                            AppendLog(">>> Workflow stopped on first failure.");
                        }

                        foreach (var child in graph.Outgoing[node])
                        {
                            TryResolve(child);
                        }

                        continue;
                    }

                    AppendLog($">>> Starting node: {scriptName}");
                    var task = RunProcessAsync(node, scriptName, $"[{scriptName}] ");
                    running[task] = node;
                }

                if (running.Count == 0)
                {
                    if (_stopRequested)
                    {
                        break;
                    }

                    continue;
                }

                var completedTask = await Task.WhenAny(running.Keys);
                var completedNode = running[completedTask];
                running.Remove(completedTask);

                var exitCode = await completedTask;
                lastExitCode = exitCode;

                if (_stopRequested)
                {
                    state[completedNode] = exitCode == 0 ? NodeRuntimeState.Success : NodeRuntimeState.Failed;
                }
                else if (exitCode == 0)
                {
                    state[completedNode] = NodeRuntimeState.Success;
                }
                else
                {
                    state[completedNode] = NodeRuntimeState.Failed;
                    AppendLog($">>> Node failed: {GetScriptName(completedNode)} (exit {exitCode})");

                    if (stopOnFailure)
                    {
                        _stopRequested = true;
                        AppendLog(">>> Workflow stopped on first failure.");
                        RequestStopAllProcesses();
                    }
                }

                foreach (var child in graph.Outgoing[completedNode])
                {
                    TryResolve(child);
                }
            }

            if (_stopRequested)
            {
                foreach (var node in graph.Nodes.Where(n => state[n] == NodeRuntimeState.Pending))
                {
                    state[node] = NodeRuntimeState.Skipped;
                }
                AppendLog(">>> Workflow stopped by request.");
            }
            else
            {
                AppendLog(">>> Workflow execution finished.");
            }

            LastRunText.Text = $"Last run exit code: {lastExitCode}";
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            LastRunText.Text = "Last run exit code: -1";
        }
        finally
        {
            SetExecutionState(false, "Status: Idle");
        }
    }

    private void NodeBuilderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || _scripts.Count == 0)
        {
            return;
        }

        var editor = new NodeEditorWindow(_scripts.ToList(), _workflowEdges)
        {
            Owner = this,
        };

        if (editor.ShowDialog() != true || editor.Edges is null)
        {
            return;
        }

        _workflowEdges.Clear();
        _workflowEdges.AddRange(editor.Edges);
        NormalizeWorkflowEdges();

        RebuildWorkflowPreview(editor.OrderedPaths);
        SaveCatalog();
        UpdateUiState();
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting)
        {
            return;
        }

        if (_workflowEdges.Count == 0)
        {
            MessageBox.Show("Create workflow links first, then save a preset.", "No workflow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var suggested = (WorkflowPresetComboBox.SelectedItem as WorkflowPreset)?.Name ?? string.Empty;
        var presetName = PromptPresetName(suggested);
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return;
        }

        var existing = _workflowPresets.FirstOrDefault(x => string.Equals(x.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var overwrite = MessageBox.Show(
                $"Preset '{existing.Name}' already exists. Overwrite?",
                "Overwrite preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (overwrite != MessageBoxResult.Yes)
            {
                return;
            }

            existing.WorkflowEdges = CloneEdges(_workflowEdges);
            existing.StopOnFirstFailure = StopOnFirstFailureCheckBox.IsChecked == true;
            existing.MaxParallel = GetMaxParallel();
            WorkflowPresetComboBox.SelectedItem = existing;
        }
        else
        {
            var preset = new WorkflowPreset
            {
                Name = presetName,
                WorkflowEdges = CloneEdges(_workflowEdges),
                StopOnFirstFailure = StopOnFirstFailureCheckBox.IsChecked == true,
                MaxParallel = GetMaxParallel(),
            };
            _workflowPresets.Add(preset);
            WorkflowPresetComboBox.SelectedItem = preset;
        }

        SaveCatalog();
        UpdateUiState();
    }

    private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || WorkflowPresetComboBox.SelectedItem is not WorkflowPreset preset)
        {
            return;
        }

        _workflowEdges.Clear();
        _workflowEdges.AddRange(CloneEdges(preset.WorkflowEdges));
        NormalizeWorkflowEdges();

        StopOnFirstFailureCheckBox.IsChecked = preset.StopOnFirstFailure;
        SetMaxParallelSelection(preset.MaxParallel <= 0 ? 2 : preset.MaxParallel);

        RebuildWorkflowPreview();
        SaveCatalog();
        UpdateUiState();
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || WorkflowPresetComboBox.SelectedItem is not WorkflowPreset preset)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete preset '{preset.Name}'?",
            "Delete preset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _workflowPresets.Remove(preset);
        SaveCatalog();
        UpdateUiState();
    }

    private void ClearWorkflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting)
        {
            return;
        }

        _workflowEdges.Clear();
        _workflowPreview.Clear();
        SaveCatalog();
        UpdateUiState();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isExecuting)
        {
            return;
        }

        _stopRequested = true;
        AppendLog(">>> Stop requested...");
        RequestStopAllProcesses();
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void ScriptListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionDetails();
        UpdateUiState();
    }

    private void ScriptColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || header.Tag is not string property)
        {
            return;
        }

        var direction = ListSortDirection.Ascending;
        if (string.Equals(_sortProperty, property, StringComparison.Ordinal))
        {
            direction = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }

        ApplySortView(property, direction);
    }

    private void StopOnFirstFailureCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            SaveCatalog();
        }
    }

    private void MaxParallelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SaveCatalog();
        }
    }

    private void WorkflowPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateUiState();
        }
    }

    private async Task<int> RunProcessAsync(string scriptPath, string scriptName, string linePrefix)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            },
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Dispatcher.Invoke(() => AppendLog(linePrefix + args.Data));
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Dispatcher.Invoke(() => AppendLog(linePrefix + args.Data));
            }
        };

        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        lock (_processLock)
        {
            _runningProcesses.Add(process);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process for {scriptName}.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return await tcs.Task;
        }
        finally
        {
            lock (_processLock)
            {
                _runningProcesses.Remove(process);
            }
            process.Dispose();
        }
    }

    private void RequestStopAllProcesses()
    {
        List<Process> running;
        lock (_processLock)
        {
            running = _runningProcesses.ToList();
        }

        foreach (var process in running)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort stop
            }
        }
    }

    private void UpdateSelectionDetails()
    {
        if (ScriptListView.SelectedItem is ScriptEntry selected)
        {
            SelectedScriptText.Text = $"Selected: {selected.Name}";
            SelectedPathTextBox.Text = selected.Path;
            return;
        }

        SelectedScriptText.Text = "Selected: None";
        SelectedPathTextBox.Text = string.Empty;
    }

    private void UpdateUiState()
    {
        var hasSelection = ScriptListView.SelectedItem is ScriptEntry;

        AddScriptButton.IsEnabled = !_isExecuting;
        RemoveScriptButton.IsEnabled = !_isExecuting && hasSelection;
        ChangePathButton.IsEnabled = !_isExecuting && hasSelection;
        RunButton.IsEnabled = !_isExecuting && hasSelection;

        NodeBuilderButton.IsEnabled = !_isExecuting && _scripts.Count > 0;
        RunWorkflowButton.IsEnabled = !_isExecuting && _workflowEdges.Count > 0;
        ClearWorkflowButton.IsEnabled = !_isExecuting && _workflowEdges.Count > 0;
        SavePresetButton.IsEnabled = !_isExecuting && _workflowEdges.Count > 0;
        WorkflowPresetComboBox.IsEnabled = !_isExecuting && _workflowPresets.Count > 0;
        LoadPresetButton.IsEnabled = !_isExecuting && WorkflowPresetComboBox.SelectedItem is WorkflowPreset;
        DeletePresetButton.IsEnabled = !_isExecuting && WorkflowPresetComboBox.SelectedItem is WorkflowPreset;
        MaxParallelComboBox.IsEnabled = !_isExecuting;
        StopOnFirstFailureCheckBox.IsEnabled = !_isExecuting;

        ScriptListView.IsEnabled = !_isExecuting;
        StopButton.IsEnabled = _isExecuting;
    }

    private void SetExecutionState(bool executing, string status)
    {
        _isExecuting = executing;
        StatusText.Text = status;
        UpdateUiState();
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void LoadCatalog()
    {
        if (!File.Exists(_catalogFilePath))
        {
            TryAddDefaultScript();
            RebuildWorkflowPreview();
            SaveCatalog();
            return;
        }

        try
        {
            var json = File.ReadAllText(_catalogFilePath);

            List<ScriptEntry> loadedScripts = new();
            List<WorkflowEdge> loadedEdges = new();
            List<WorkflowPreset> loadedPresets = new();
            bool stopOnFirstFailure = true;
            int maxParallel = 2;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                loadedScripts = JsonSerializer.Deserialize<List<ScriptEntry>>(json) ?? new();
            }
            else
            {
                var state = JsonSerializer.Deserialize<CatalogState>(json) ?? new CatalogState();
                loadedScripts = state.Scripts ?? new();
                loadedEdges = state.WorkflowEdges ?? BuildEdgesFromLegacyChain(state.ChainPaths ?? new List<string>());
                loadedPresets = state.WorkflowPresets ?? new List<WorkflowPreset>();
                stopOnFirstFailure = state.StopOnFirstFailure;
                maxParallel = state.MaxParallel <= 0 ? 2 : state.MaxParallel;
            }

            LoadScripts(loadedScripts);
            _workflowEdges.Clear();
            _workflowEdges.AddRange(loadedEdges);
            NormalizeWorkflowEdges();
            LoadWorkflowPresets(loadedPresets);

            if (_scripts.Count == 0)
            {
                TryAddDefaultScript();
            }

            StopOnFirstFailureCheckBox.IsChecked = stopOnFirstFailure;
            SetMaxParallelSelection(maxParallel);
            RebuildWorkflowPreview();
        }
        catch
        {
            _scripts.Clear();
            _workflowEdges.Clear();
            _workflowPreview.Clear();
            _workflowPresets.Clear();
            TryAddDefaultScript();
            StopOnFirstFailureCheckBox.IsChecked = true;
            SetMaxParallelSelection(2);
            SaveCatalog();
        }
    }

    private void SaveCatalog()
    {
        var state = new CatalogState
        {
            Scripts = _scripts.Select(x => new ScriptEntry { Name = x.Name, Path = x.Path }).ToList(),
            WorkflowEdges = _workflowEdges.Select(x => new WorkflowEdge { FromPath = x.FromPath, ToPath = x.ToPath }).ToList(),
            WorkflowPresets = _workflowPresets.Select(x => new WorkflowPreset
            {
                Name = x.Name,
                WorkflowEdges = CloneEdges(x.WorkflowEdges),
                StopOnFirstFailure = x.StopOnFirstFailure,
                MaxParallel = x.MaxParallel,
            }).ToList(),
            StopOnFirstFailure = StopOnFirstFailureCheckBox.IsChecked == true,
            MaxParallel = GetMaxParallel(),
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_catalogFilePath, json);
    }

    private void TryAddDefaultScript()
    {
        var defaultScript = Path.Combine(AppContext.BaseDirectory, "test_script.bat");
        if (!File.Exists(defaultScript))
        {
            return;
        }

        _scripts.Add(new ScriptEntry
        {
            Name = BuildDisplayName(defaultScript),
            Path = Path.GetFullPath(defaultScript),
            DateModified = GetDateModified(defaultScript),
        });
    }

    private void LoadScripts(IEnumerable<ScriptEntry> loadedScripts)
    {
        _scripts.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in loadedScripts)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            var normalized = Path.GetFullPath(entry.Path);
            if (!seen.Add(normalized))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? BuildDisplayName(normalized) : entry.Name;
            _scripts.Add(new ScriptEntry
            {
                Name = name,
                Path = normalized,
                DateModified = GetDateModified(normalized),
            });
        }
    }

    private static List<WorkflowEdge> BuildEdgesFromLegacyChain(IReadOnlyList<string> chainPaths)
    {
        var edges = new List<WorkflowEdge>();
        for (int i = 0; i < chainPaths.Count - 1; i++)
        {
            if (string.IsNullOrWhiteSpace(chainPaths[i]) || string.IsNullOrWhiteSpace(chainPaths[i + 1]))
            {
                continue;
            }

            edges.Add(new WorkflowEdge
            {
                FromPath = Path.GetFullPath(chainPaths[i]),
                ToPath = Path.GetFullPath(chainPaths[i + 1]),
            });
        }

        return edges;
    }

    private void RemoveWorkflowEdgesByPath(string path)
    {
        _workflowEdges.RemoveAll(x =>
            string.Equals(x.FromPath, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.ToPath, path, StringComparison.OrdinalIgnoreCase)
        );
    }

    private void UpdateWorkflowEdgesForPath(string oldPath, string newPath)
    {
        foreach (var edge in _workflowEdges)
        {
            if (string.Equals(edge.FromPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                edge.FromPath = newPath;
            }
            if (string.Equals(edge.ToPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                edge.ToPath = newPath;
            }
        }
    }

    private void NormalizeWorkflowEdges()
    {
        var normalized = _workflowEdges
            .Where(x => !string.IsNullOrWhiteSpace(x.FromPath) && !string.IsNullOrWhiteSpace(x.ToPath))
            .Select(x => new WorkflowEdge
            {
                FromPath = Path.GetFullPath(x.FromPath),
                ToPath = Path.GetFullPath(x.ToPath),
            })
            .Where(x => !string.Equals(x.FromPath, x.ToPath, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => $"{x.FromPath}=>{x.ToPath}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        _workflowEdges.Clear();
        _workflowEdges.AddRange(normalized);
    }

    private void LoadWorkflowPresets(IEnumerable<WorkflowPreset> presets)
    {
        _workflowPresets.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name))
            {
                continue;
            }
            if (!seen.Add(preset.Name))
            {
                continue;
            }

            var normalizedEdges = (preset.WorkflowEdges ?? new List<WorkflowEdge>())
                .Where(x => !string.IsNullOrWhiteSpace(x.FromPath) && !string.IsNullOrWhiteSpace(x.ToPath))
                .Select(x => new WorkflowEdge
                {
                    FromPath = Path.GetFullPath(x.FromPath),
                    ToPath = Path.GetFullPath(x.ToPath),
                })
                .Where(x => !string.Equals(x.FromPath, x.ToPath, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(x => $"{x.FromPath}=>{x.ToPath}", StringComparer.OrdinalIgnoreCase)
                .ToList();

            _workflowPresets.Add(new WorkflowPreset
            {
                Name = preset.Name.Trim(),
                WorkflowEdges = normalizedEdges,
                StopOnFirstFailure = preset.StopOnFirstFailure,
                MaxParallel = preset.MaxParallel <= 0 ? 2 : preset.MaxParallel,
            });
        }
    }

    private void RebuildWorkflowPreview(IReadOnlyList<string>? explicitOrder = null)
    {
        _workflowPreview.Clear();

        if (_workflowEdges.Count == 0)
        {
            return;
        }

        var graph = BuildGraphFromEdges();
        var ordered = explicitOrder?.ToList() ?? TopologicalOrder(graph);

        if (ordered.Count == 0)
        {
            _workflowPreview.Add(new WorkflowPreviewStep { Display = "Invalid workflow: cycle detected." });
            return;
        }

        int i = 1;
        foreach (var path in ordered)
        {
            var name = GetScriptName(path);
            var deps = graph.Incoming.TryGetValue(path, out var incoming) ? incoming.Count : 0;
            _workflowPreview.Add(new WorkflowPreviewStep
            {
                Display = $"{i}. {name}  (depends on: {deps})"
            });
            i++;
        }
    }

    private WorkflowGraph BuildGraphFromEdges()
    {
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in _workflowEdges)
        {
            nodes.Add(edge.FromPath);
            nodes.Add(edge.ToPath);
        }

        var incoming = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var outgoing = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in _workflowEdges)
        {
            outgoing[edge.FromPath].Add(edge.ToPath);
            incoming[edge.ToPath].Add(edge.FromPath);
        }

        return new WorkflowGraph(nodes, incoming, outgoing);
    }

    private static bool IsAcyclic(WorkflowGraph graph)
    {
        return TopologicalOrder(graph).Count == graph.Nodes.Count;
    }

    private static List<string> TopologicalOrder(WorkflowGraph graph)
    {
        var indegree = graph.Nodes.ToDictionary(n => n, n => graph.Incoming[n].Count, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
        var ordered = new List<string>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            ordered.Add(node);

            foreach (var child in graph.Outgoing[node])
            {
                indegree[child]--;
                if (indegree[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return ordered;
    }

    private void ApplySortView(string property, ListSortDirection direction, string? selectedPath = null)
    {
        selectedPath ??= (ScriptListView.SelectedItem as ScriptEntry)?.Path;

        var view = CollectionViewSource.GetDefaultView(ScriptListView.ItemsSource);
        if (view is null)
        {
            return;
        }

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(property, direction));
        view.Refresh();

        _sortProperty = property;
        _sortDirection = direction;

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        var selected = _scripts.FirstOrDefault(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            ScriptListView.SelectedItem = selected;
            ScriptListView.ScrollIntoView(selected);
        }
    }

    private static string? PickBatFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Batch Files (*.bat)|*.bat|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string BuildDisplayName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName.Replace("_", " ");
    }

    private string GetScriptName(string path)
    {
        var match = _scripts.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? BuildDisplayName(path);
    }

    private static DateTime? GetDateModified(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

    private int GetMaxParallel()
    {
        if (MaxParallelComboBox.SelectedValue is string value && int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        if (MaxParallelComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out parsed) && parsed > 0)
        {
            return parsed;
        }

        return 2;
    }

    private void SetMaxParallelSelection(int value)
    {
        foreach (var obj in MaxParallelComboBox.Items)
        {
            if (obj is ComboBoxItem item && item.Tag?.ToString() == value.ToString())
            {
                MaxParallelComboBox.SelectedItem = item;
                return;
            }
        }

        MaxParallelComboBox.SelectedIndex = 1;
    }

    private static List<WorkflowEdge> CloneEdges(IEnumerable<WorkflowEdge> edges)
    {
        return edges
            .Select(x => new WorkflowEdge { FromPath = x.FromPath, ToPath = x.ToPath })
            .ToList();
    }

    private string? PromptPresetName(string suggested)
    {
        var dialog = new Window
        {
            Title = "Save Workflow Preset",
            Width = 420,
            Height = 170,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = System.Windows.Media.Brushes.White,
        };

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Preset name",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var nameBox = new TextBox
        {
            Text = suggested,
            Height = 28,
            Padding = new Thickness(6, 4, 6, 4),
        };
        Grid.SetRow(nameBox, 1);
        root.Children.Add(nameBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var cancelBtn = new Button { Content = "Cancel", Width = 82, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        var saveBtn = new Button { Content = "Save", Width = 82 };
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show(dialog, "Enter a preset name.", "Missing name", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            dialog.DialogResult = true;
        };

        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(saveBtn);
        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        dialog.Content = root;
        nameBox.Focus();
        nameBox.SelectAll();

        return dialog.ShowDialog() == true ? nameBox.Text.Trim() : null;
    }
}

public sealed class ScriptEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime? DateModified { get; set; }
}

public sealed class WorkflowEdge
{
    public string FromPath { get; set; } = string.Empty;
    public string ToPath { get; set; } = string.Empty;
}

public sealed class WorkflowPreviewStep
{
    public string Display { get; set; } = string.Empty;
}

public sealed class CatalogState
{
    public List<ScriptEntry>? Scripts { get; set; }
    public List<WorkflowEdge>? WorkflowEdges { get; set; }
    public List<WorkflowPreset>? WorkflowPresets { get; set; }
    public List<string>? ChainPaths { get; set; }
    public bool StopOnFirstFailure { get; set; } = true;
    public int MaxParallel { get; set; } = 2;
}

public sealed class WorkflowPreset
{
    public string Name { get; set; } = string.Empty;
    public List<WorkflowEdge> WorkflowEdges { get; set; } = new();
    public bool StopOnFirstFailure { get; set; } = true;
    public int MaxParallel { get; set; } = 2;
}

public sealed class WorkflowGraph
{
    public HashSet<string> Nodes { get; }
    public Dictionary<string, List<string>> Incoming { get; }
    public Dictionary<string, List<string>> Outgoing { get; }

    public WorkflowGraph(HashSet<string> nodes, Dictionary<string, List<string>> incoming, Dictionary<string, List<string>> outgoing)
    {
        Nodes = nodes;
        Incoming = incoming;
        Outgoing = outgoing;
    }
}

public enum NodeRuntimeState
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
}
