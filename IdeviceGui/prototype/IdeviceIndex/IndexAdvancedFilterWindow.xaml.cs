using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdeviceIndex.Resources;

namespace IdeviceIndex;

internal partial class IndexAdvancedFilterWindow : Window
{
    private readonly Action<IndexAdvancedFilterState> _onApply;
    private readonly IndexAdvancedFilterState _working;
    private readonly List<GroupUi> _groups = [];

    private static (IndexFilterColumn Column, string Label)[] GetColumnOptions() =>
    [
        (IndexFilterColumn.Title, Strings.Column_Title),
        (IndexFilterColumn.BundleId, "BundleId"),
        (IndexFilterColumn.Version, Strings.Column_Version),
        (IndexFilterColumn.MinOs, Strings.Column_MinOs),
        (IndexFilterColumn.SizeMb, Strings.Filter_Col_SizeMb),
        (IndexFilterColumn.DeviceFamily, Strings.Column_DeviceFamily),
        (IndexFilterColumn.Category, Strings.Column_Category),
        (IndexFilterColumn.Developer, Strings.Filter_Col_Developer),
        (IndexFilterColumn.Source, Strings.Filter_Col_Source),
        (IndexFilterColumn.Description, Strings.Filter_Col_Description),
    ];

    public IndexAdvancedFilterWindow(
        IndexAdvancedFilterState state,
        Action<IndexAdvancedFilterState> onApply,
        Window? owner = null)
    {
        InitializeComponent();
        Owner = owner;
        _onApply = onApply;
        _working = state.Clone();
        if (_working.Groups.Count == 0)
            _working.Groups.Add(new IndexAdvancedFilterGroup { Conditions = [new IndexAdvancedFilterCondition()] });

        foreach (var group in _working.Groups)
            AddGroupUi(group);
    }

    private void AddGroupUi(IndexAdvancedFilterGroup group)
    {
        var box = new GroupBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = Strings.Format(nameof(Strings.Filter_Group_HeaderFormat), _groups.Count + 1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = Strings.Filter_Group_JoinLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        });
        var join = new ComboBox
        {
            Width = 74,
            ItemsSource = new[] { Strings.Filter_Join_And, Strings.Filter_Join_Or },
            SelectedIndex = group.Join == IndexFilterJoin.Or ? 1 : 0,
        };
        header.Children.Add(join);
        var add = new Button
        {
            Content = Strings.Filter_AddCondition,
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(12, 0, 0, 0),
        };
        header.Children.Add(add);
        if (_groups.Count > 0)
        {
            var remove = new Button
            {
                Content = Strings.Filter_RemoveGroup,
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(8, 0, 0, 0),
            };
            header.Children.Add(remove);
            remove.Click += (_, _) => RemoveGroup(box);
        }

        box.Header = header;
        var rows = new StackPanel();
        box.Content = rows;
        GroupsPanel.Children.Add(box);

        var ui = new GroupUi(box, join, rows);
        _groups.Add(ui);
        add.Click += (_, _) => AddConditionUi(ui, new IndexAdvancedFilterCondition());

        var initialConditions = group.Conditions.Count > 0
            ? group.Conditions
            : [new IndexAdvancedFilterCondition()];
        foreach (var condition in initialConditions)
            AddConditionUi(ui, condition);
    }

    private void AddConditionUi(GroupUi group, IndexAdvancedFilterCondition condition)
    {
        var row = new ConditionUi(group, condition);
        group.Rows.Add(row);
        group.RowsPanel.Children.Add(row.Root);
    }

    private void RemoveGroup(GroupBox box)
    {
        var index = _groups.FindIndex(g => ReferenceEquals(g.Box, box));
        if (index < 0) return;
        _groups.RemoveAt(index);
        GroupsPanel.Children.Remove(box);
        for (var i = 0; i < _groups.Count; i++)
        {
            if (_groups[i].Box.Header is StackPanel header && header.Children[0] is TextBlock label)
                label.Text = Strings.Format(nameof(Strings.Filter_Group_HeaderFormat), i + 1);
        }
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
        => AddGroupUi(new IndexAdvancedFilterGroup { Conditions = [new IndexAdvancedFilterCondition()] });

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        _groups.Clear();
        GroupsPanel.Children.Clear();
        AddGroupUi(new IndexAdvancedFilterGroup { Conditions = [new IndexAdvancedFilterCondition()] });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var state = CollectState();
        _onApply(state);
        Close();
    }

    private IndexAdvancedFilterState CollectState()
    {
        var state = new IndexAdvancedFilterState { Enabled = _working.Enabled };
        foreach (var group in _groups)
        {
            var filterGroup = new IndexAdvancedFilterGroup
            {
                Join = group.Join.SelectedIndex == 1 ? IndexFilterJoin.Or : IndexFilterJoin.And,
            };
            foreach (var row in group.Rows)
            {
                var condition = row.Collect();
                if (!condition.IsBlank) filterGroup.Conditions.Add(condition);
            }
            if (filterGroup.Conditions.Count > 0)
                state.Groups.Add(filterGroup);
        }
        return state;
    }

    private sealed class GroupUi(GroupBox box, ComboBox join, StackPanel rowsPanel)
    {
        public GroupBox Box { get; } = box;
        public ComboBox Join { get; } = join;
        public StackPanel RowsPanel { get; } = rowsPanel;
        public List<ConditionUi> Rows { get; } = [];
    }

    private sealed class ConditionUi
    {
        private readonly IndexAdvancedFilterCondition _source;
        private readonly (IndexFilterColumn Column, string Label)[] _columnOptions;
        private readonly ComboBox _column;
        private readonly Border _editorHost;
        private readonly CheckBox _enabled;
        private readonly GroupUi _group;

        private ComboBox? _textMode;
        private TextBox? _text;
        private ComboBox? _versionMode;
        private TextBox? _versionExact;
        private StackPanel? _rangePanel;
        private TextBox? _min;
        private TextBox? _max;
        private readonly List<CheckBox> _deviceChecks = [];

        public ConditionUi(GroupUi group, IndexAdvancedFilterCondition condition)
        {
            _group = group;
            _source = condition;
            _columnOptions = GetColumnOptions();

            _enabled = new CheckBox
            {
                IsChecked = condition.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _enabled.Checked += (_, _) => ApplyEnabled();
            _enabled.Unchecked += (_, _) => ApplyEnabled();

            _column = new ComboBox
            {
                Width = 140,
                ItemsSource = _columnOptions.Select(o => o.Label).ToArray(),
                SelectedIndex = Math.Max(0, Array.FindIndex(_columnOptions, o => o.Column == condition.Column)),
                Margin = new Thickness(6, 0, 6, 0),
            };
            _column.SelectionChanged += (_, _) => BuildEditor();

            var remove = new Button
            {
                Content = "×",
                Width = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
            };
            remove.Click += (_, _) => Remove();

            _editorHost = new Border { Padding = new Thickness(2) };
            var root = new StackPanel { Orientation = Orientation.Horizontal };
            root.Children.Add(_enabled);
            root.Children.Add(_column);
            root.Children.Add(_editorHost);
            root.Children.Add(remove);

            Root = new Border
            {
                Child = root,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
            };
            BuildEditor();
            ApplyEnabled();
        }

        public Border Root { get; }

        private IndexFilterColumn CurrentColumn =>
            _columnOptions[Math.Max(0, _column.SelectedIndex)].Column;

        private void ApplyEnabled()
        {
            var isOn = _enabled.IsChecked == true;
            Root.Opacity = isOn ? 1.0 : 0.45;
            _column.IsEnabled = isOn;
            _editorHost.IsEnabled = isOn;
        }

        private void Remove()
        {
            _group.Rows.Remove(this);
            _group.RowsPanel.Children.Remove(Root);
        }

        private void BuildEditor()
        {
            _editorHost.Child = null;
            _textMode = null;
            _text = null;
            _versionMode = null;
            _versionExact = null;
            _rangePanel = null;
            _min = null;
            _max = null;
            _deviceChecks.Clear();

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            switch (CurrentColumn)
            {
                case IndexFilterColumn.Title:
                case IndexFilterColumn.BundleId:
                case IndexFilterColumn.Category:
                case IndexFilterColumn.Developer:
                case IndexFilterColumn.Source:
                case IndexFilterColumn.Description:
                {
                    _textMode = new ComboBox
                    {
                        Width = 96,
                        ItemsSource = new[] { Strings.Search_ModeContains, Strings.Search_ModeExact },
                        SelectedIndex = _source.TextMode == IndexTextFilterMode.Exact ? 1 : 0,
                    };
                    _text = new TextBox
                    {
                        Width = 260,
                        Text = _source.Text,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    panel.Children.Add(_textMode);
                    panel.Children.Add(_text);
                    break;
                }
                case IndexFilterColumn.Version:
                case IndexFilterColumn.MinOs:
                {
                    _versionMode = new ComboBox
                    {
                        Width = 96,
                        ItemsSource = new[] { Strings.Search_ModeExact, Strings.Filter_Mode_Range },
                        SelectedIndex = (string.IsNullOrWhiteSpace(_source.Min) && string.IsNullOrWhiteSpace(_source.Max)) ? 0 : 1,
                    };
                    _versionExact = new TextBox
                    {
                        Width = 120,
                        Text = _source.Text,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    _rangePanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(6, 0, 0, 0),
                    };
                    _min = new TextBox { Width = 80, Text = _source.Min, VerticalContentAlignment = VerticalAlignment.Center };
                    _max = new TextBox { Width = 80, Text = _source.Max, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
                    _rangePanel.Children.Add(_min);
                    _rangePanel.Children.Add(new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center });
                    _rangePanel.Children.Add(_max);
                    _versionMode.SelectionChanged += (_, _) => ToggleVersionMode();
                    panel.Children.Add(_versionMode);
                    panel.Children.Add(_versionExact);
                    panel.Children.Add(_rangePanel);
                    ToggleVersionMode();
                    break;
                }
                case IndexFilterColumn.SizeMb:
                {
                    _min = new TextBox { Width = 90, Text = _source.Min, VerticalContentAlignment = VerticalAlignment.Center };
                    _max = new TextBox { Width = 90, Text = _source.Max, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
                    panel.Children.Add(new TextBlock { Text = "MB ", VerticalAlignment = VerticalAlignment.Center });
                    panel.Children.Add(_min);
                    panel.Children.Add(new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center });
                    panel.Children.Add(_max);
                    panel.Children.Add(new TextBlock { Text = " MB", VerticalAlignment = VerticalAlignment.Center });
                    break;
                }
                case IndexFilterColumn.DeviceFamily:
                {
                    foreach (var name in new[] { "iPhone", "iPad", "AppleTV" })
                    {
                        var cb = new CheckBox
                        {
                            Content = name,
                            Tag = name,
                            Margin = new Thickness(0, 0, 12, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            IsChecked = _source.Selected.Contains(name, StringComparer.OrdinalIgnoreCase),
                        };
                        _deviceChecks.Add(cb);
                        panel.Children.Add(cb);
                    }
                    break;
                }
            }

            _editorHost.Child = panel;
        }

        private void ToggleVersionMode()
        {
            if (_versionMode is null || _versionExact is null || _rangePanel is null) return;
            var isRange = _versionMode.SelectedIndex == 1;
            _versionExact.Visibility = isRange ? Visibility.Collapsed : Visibility.Visible;
            _rangePanel.Visibility = isRange ? Visibility.Visible : Visibility.Collapsed;
        }

        public IndexAdvancedFilterCondition Collect()
        {
            var col = CurrentColumn;
            var cond = new IndexAdvancedFilterCondition
            {
                Column = col,
                Enabled = _enabled.IsChecked == true,
                TextMode = _textMode?.SelectedIndex == 1 ? IndexTextFilterMode.Exact : IndexTextFilterMode.Contains,
                Text = _text?.Text?.Trim() ?? "",
                Min = _min?.Text?.Trim() ?? "",
                Max = _max?.Text?.Trim() ?? "",
                Selected = _deviceChecks
                    .Where(c => c.IsChecked == true)
                    .Select(c => c.Tag as string ?? "")
                    .Where(s => s.Length > 0)
                    .ToList(),
            };

            if (col is IndexFilterColumn.Version or IndexFilterColumn.MinOs)
            {
                if (_versionMode?.SelectedIndex == 0)
                {
                    cond.Text = _versionExact?.Text?.Trim() ?? "";
                    cond.Min = "";
                    cond.Max = "";
                }
                else
                {
                    cond.Text = "";
                }
            }

            return cond;
        }
    }
}
