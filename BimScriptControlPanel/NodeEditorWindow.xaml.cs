using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BimScriptControlPanel;

public partial class NodeEditorWindow : Window
{
    private const double NodeWidth = 190;
    private const double NodeHeight = 64;

    private readonly List<ScriptEntry> _scripts;
    private readonly Dictionary<string, Border> _nodeBorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _nodePositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _outgoing = new(StringComparer.OrdinalIgnoreCase);

    private string? _selectedSourcePath;
    private string? _draggingPath;
    private Point _dragStartPoint;
    private Point _dragStartNodePoint;

    public List<WorkflowEdge>? Edges { get; private set; }
    public List<string>? OrderedPaths { get; private set; }

    public NodeEditorWindow(IReadOnlyList<ScriptEntry> scripts, IReadOnlyList<WorkflowEdge> existingEdges)
    {
        InitializeComponent();

        _scripts = scripts
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScriptEntry { Name = g.First().Name, Path = g.First().Path })
            .ToList();

        foreach (var script in _scripts)
        {
            _outgoing[script.Path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        NodeCanvas.SizeChanged += (_, _) => RedrawEdges();

        BuildNodes();
        SeedLinks(existingEdges);
        UpdateSourceHighlight();
        RedrawEdges();
    }

    private void BuildNodes()
    {
        NodeCanvas.Children.Clear();
        _nodeBorders.Clear();
        _nodePositions.Clear();

        int columns = 3;
        double startX = 24;
        double startY = 20;
        double gapX = 34;
        double gapY = 22;

        for (int i = 0; i < _scripts.Count; i++)
        {
            var script = _scripts[i];
            int col = i % columns;
            int row = i / columns;

            double x = startX + col * (NodeWidth + gapX);
            double y = startY + row * (NodeHeight + gapY);
            _nodePositions[script.Path] = new Point(x, y);

            var border = CreateNode(script);
            _nodeBorders[script.Path] = border;
            NodeCanvas.Children.Add(border);
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
        }

        InfoText.Text = _scripts.Count == 0
            ? "No scripts available to link."
            : "Right-click a source node to start linking.";
    }

    private Border CreateNode(ScriptEntry script)
    {
        var nameText = new TextBlock
        {
            Text = script.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A")),
            Margin = new Thickness(0, 0, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var pathText = new TextBlock
        {
            Text = script.Path,
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        stack.Children.Add(nameText);
        stack.Children.Add(pathText);

        var border = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
            Cursor = Cursors.SizeAll,
            Tag = script.Path,
        };

        border.MouseLeftButtonDown += Node_MouseLeftButtonDown;
        border.MouseMove += Node_MouseMove;
        border.MouseLeftButtonUp += Node_MouseLeftButtonUp;
        border.MouseRightButtonUp += Node_MouseRightButtonUp;

        return border;
    }

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string path)
        {
            return;
        }

        _draggingPath = path;
        _dragStartPoint = e.GetPosition(NodeCanvas);
        _dragStartNodePoint = _nodePositions[path];
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPath is null || sender is not Border border || !border.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(NodeCanvas);
        double dx = current.X - _dragStartPoint.X;
        double dy = current.Y - _dragStartPoint.Y;

        double x = Math.Max(6, _dragStartNodePoint.X + dx);
        double y = Math.Max(6, _dragStartNodePoint.Y + dy);

        _nodePositions[_draggingPath] = new Point(x, y);
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        RedrawEdges();
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        if (border.IsMouseCaptured)
        {
            border.ReleaseMouseCapture();
        }

        _draggingPath = null;
        e.Handled = true;
    }

    private void Node_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string path)
        {
            return;
        }

        TryLinkWithRightClick(path);
        e.Handled = true;
    }

    private void TryLinkWithRightClick(string path)
    {
        if (_selectedSourcePath is null)
        {
            _selectedSourcePath = path;
            UpdateSourceHighlight();
            InfoText.Text = "Source selected. Right-click a target node to toggle link.";
            return;
        }

        if (string.Equals(_selectedSourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSourcePath = null;
            UpdateSourceHighlight();
            InfoText.Text = "Source cleared.";
            return;
        }

        if (_outgoing[_selectedSourcePath].Contains(path))
        {
            _outgoing[_selectedSourcePath].Remove(path);
            _selectedSourcePath = null;
            UpdateSourceHighlight();
            RedrawEdges();
            InfoText.Text = "Link removed.";
            return;
        }

        if (WouldCreateCycle(_selectedSourcePath, path))
        {
            InfoText.Text = "Link rejected: this would create a cycle.";
            return;
        }

        _outgoing[_selectedSourcePath].Add(path);
        _selectedSourcePath = null;
        UpdateSourceHighlight();
        RedrawEdges();
        InfoText.Text = "Link added.";
    }

    private bool WouldCreateCycle(string sourcePath, string targetPath)
    {
        return PathExists(targetPath, sourcePath);
    }

    private bool PathExists(string start, string goal)
    {
        var stack = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        stack.Push(start);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, goal, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!_outgoing.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                stack.Push(child);
            }
        }

        return false;
    }

    private void SeedLinks(IReadOnlyList<WorkflowEdge> existingEdges)
    {
        foreach (var edge in existingEdges)
        {
            if (string.IsNullOrWhiteSpace(edge.FromPath) || string.IsNullOrWhiteSpace(edge.ToPath))
            {
                continue;
            }

            var from = edge.FromPath;
            var to = edge.ToPath;

            if (!_outgoing.ContainsKey(from) || !_outgoing.ContainsKey(to))
            {
                continue;
            }

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (WouldCreateCycle(from, to))
            {
                continue;
            }

            _outgoing[from].Add(to);
        }
    }

    private void RedrawEdges()
    {
        EdgeCanvas.Children.Clear();

        foreach (var source in _outgoing.Keys)
        {
            foreach (var target in _outgoing[source])
            {
                if (!_nodePositions.TryGetValue(source, out var from))
                {
                    continue;
                }
                if (!_nodePositions.TryGetValue(target, out var to))
                {
                    continue;
                }

                var start = new Point(from.X + NodeWidth, from.Y + NodeHeight / 2);
                var end = new Point(to.X, to.Y + NodeHeight / 2);

                var line = new Line
                {
                    X1 = start.X,
                    Y1 = start.Y,
                    X2 = end.X,
                    Y2 = end.Y,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB")),
                    StrokeThickness = 2,
                };

                EdgeCanvas.Children.Add(line);
                EdgeCanvas.Children.Add(CreateArrowHead(start, end));
            }
        }
    }

    private static Polygon CreateArrowHead(Point start, Point end)
    {
        var vector = end - start;
        if (vector.Length < 0.1)
        {
            vector = new Vector(1, 0);
        }

        vector.Normalize();
        var perp = new Vector(-vector.Y, vector.X);

        var tip = end;
        var baseCenter = end - vector * 11;
        var left = baseCenter + perp * 5;
        var right = baseCenter - perp * 5;

        return new Polygon
        {
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB")),
            Points = new PointCollection { tip, left, right },
        };
    }

    private void UpdateSourceHighlight()
    {
        foreach (var pair in _nodeBorders)
        {
            bool isSelected = _selectedSourcePath is not null && string.Equals(pair.Key, _selectedSourcePath, StringComparison.OrdinalIgnoreCase);
            pair.Value.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSelected ? "#2563EB" : "#CBD5E1"));
            pair.Value.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
        }
    }

    private void AutoLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        int index = 0;
        int columns = Math.Max(1, (int)((NodeCanvas.ActualWidth - 30) / (NodeWidth + 28)));

        double startX = 16;
        double startY = 14;
        double gapX = 28;
        double gapY = 20;

        foreach (var script in _scripts)
        {
            int col = index % columns;
            int row = index / columns;

            double x = startX + col * (NodeWidth + gapX);
            double y = startY + row * (NodeHeight + gapY);

            _nodePositions[script.Path] = new Point(x, y);
            var border = _nodeBorders[script.Path];
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            index++;
        }

        RedrawEdges();
        InfoText.Text = "Auto layout applied.";
    }

    private void ClearLinksButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var source in _outgoing.Keys.ToList())
        {
            _outgoing[source].Clear();
        }

        _selectedSourcePath = null;
        UpdateSourceHighlight();
        RedrawEdges();
        InfoText.Text = "All links cleared.";
    }

    private void RemoveSourceLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSourcePath is null)
        {
            InfoText.Text = "Select a source node first (right-click).";
            return;
        }

        if (_outgoing[_selectedSourcePath].Count == 0)
        {
            InfoText.Text = "Selected source has no outgoing links.";
            return;
        }

        _outgoing[_selectedSourcePath].Clear();
        RedrawEdges();
        InfoText.Text = "Outgoing links removed for selected source.";
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var edges = BuildEdgeList();
        if (edges.Count == 0)
        {
            MessageBox.Show(
                "No links defined. Create at least one link first.",
                "No workflow",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            return;
        }

        var ordered = BuildTopologicalOrder(edges);
        if (ordered.Count == 0)
        {
            MessageBox.Show(
                "Workflow is invalid (cycle detected).",
                "Invalid workflow",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return;
        }

        Edges = edges;
        OrderedPaths = ordered;
        DialogResult = true;
        Close();
    }

    private List<WorkflowEdge> BuildEdgeList()
    {
        var edges = new List<WorkflowEdge>();
        foreach (var source in _outgoing.Keys)
        {
            foreach (var target in _outgoing[source])
            {
                edges.Add(new WorkflowEdge { FromPath = source, ToPath = target });
            }
        }

        return edges;
    }

    private static List<string> BuildTopologicalOrder(IReadOnlyList<WorkflowEdge> edges)
    {
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            nodes.Add(edge.FromPath);
            nodes.Add(edge.ToPath);
        }

        var incomingCount = nodes.ToDictionary(n => n, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            outgoing[edge.FromPath].Add(edge.ToPath);
            incomingCount[edge.ToPath]++;
        }

        var queue = new Queue<string>(incomingCount.Where(x => x.Value == 0).Select(x => x.Key));
        var ordered = new List<string>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            ordered.Add(node);

            foreach (var child in outgoing[node])
            {
                incomingCount[child]--;
                if (incomingCount[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return ordered.Count == nodes.Count ? ordered : new List<string>();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
