using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Lume.Core;
namespace Lume.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlyList<Collection> ViewCollections() => organizer.State.Configuration.Collections
        .Where(c => page == "收件箱" ? c.Id == "inbox" : selectedMode == 0 || selectedMode == 1 && c.InWork || selectedMode == 2 && c.InPresentation).ToList();

    private UIElement BuildBoard()
    {
        var collections = ViewCollections();
        if (activeCollection != null && !collections.Any(c => c.Id == activeCollection)) activeCollection = null;
        var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new(192) }); layout.ColumnDefinitions.Add(new());
        var rail = new DockPanel { Margin = new(0, 0, 20, 0) };
        var railTitle = Ui.Text("分区", Tokens.Label, Ui.Muted); railTitle.Margin = new(12, 4, 0, 12); DockPanel.SetDock(railTitle, Dock.Top); rail.Children.Add(railTitle);
        var add = Ui.Button("＋ 新建分区", AddCollection); add.Margin = new(0, 12, 0, 0); add.BorderThickness = new(0); add.Background = Brushes.Transparent; DockPanel.SetDock(add, Dock.Bottom); rail.Children.Add(add);
        var links = new StackPanel();
        var grouped = collections.Select(c => (Collection: c, Files: organizer.CollectionFiles(c.Id, search.Text))).ToList();
        var all = grouped.SelectMany(g => g.Files).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        void Link(string? id, string name, string color, int count, bool acceptsDrop)
        {
            var button = Ui.Button("", () => { activeCollection = id; filePage = 0; boardSelection.Clear(); Render(); });
            var row = new DockPanel(); var countText = Ui.Text(count.ToString(), Tokens.Secondary, Ui.Muted); DockPanel.SetDock(countText, Dock.Right); row.Children.Add(countText);
            var marker = new Border { Width = 6, Height = 6, CornerRadius = new(3), Background = Ui.Brush(color), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(marker, Dock.Left); row.Children.Add(marker);
            var label = Ui.Text(name, Tokens.Secondary, activeCollection == id ? Ui.Accent : Ui.Ink, activeCollection == id); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(label);
            button.Content = row; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new(12, 11, 12, 11); button.Margin = new(0, 0, 0, 4);
            button.Background = activeCollection == id ? Tokens.Primary100Brush : Brushes.Transparent; button.BorderThickness = new(0); button.ToolTip = name;
            if (acceptsDrop && id != null) EnableCollectionDrop(button, id);
            links.Children.Add(button);
        }
        Link(null, "全部文件", Tokens.Theme.Accent, all.Count, false);
        foreach (var (collection, files) in grouped) Link(collection.Id, collection.Name, collection.Color, files.Count, collection.MappedPath == null && !collection.Recent);
        rail.Children.Add(new ScrollViewer { Content = links }); layout.Children.Add(rail);
        var selected = activeCollection == null ? all : grouped.First(g => g.Collection.Id == activeCollection).Files.ToList();
        var pages = Math.Max(1, (selected.Count + FilesPerPage - 1) / FilesPerPage); filePage = Math.Clamp(filePage, 0, pages - 1);
        var visible = selected.Skip(filePage * FilesPerPage).Take(FilesPerPage).ToList(); boardSelection.SetFiles(visible);
        var body = new DockPanel { Margin = new(20) };
        var head = new DockPanel { Margin = new(0, 0, 0, 18) };
        var countLabel = Ui.Text($"{selected.Count} 项", Tokens.Secondary, Ui.Muted); DockPanel.SetDock(countLabel, Dock.Right); head.Children.Add(countLabel);
        head.Children.Add(Ui.Text(activeCollection == null ? (page == "收件箱" ? "等待归类的文件" : "全部文件") : organizer.CollectionName(activeCollection), Tokens.SectionTitle, bold: true)); DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
        var footer = new DockPanel { Margin = new(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        if (pages > 1)
        {
            var previous = Ui.Button("上一页", () => { filePage--; Render(); }); previous.IsEnabled = filePage > 0;
            var next = Ui.Button("下一页", () => { filePage++; Render(); }); next.IsEnabled = filePage + 1 < pages;
            var pager = Ui.Row(previous, Ui.Text($"{filePage + 1} / {pages}  ", Tokens.Secondary, Ui.Muted), next); DockPanel.SetDock(pager, Dock.Right); footer.Children.Add(pager);
        }
        footer.Children.Add(Ui.Text(pages > 1 ? "Ctrl+A 选择本页" : "双击打开 · 空格预览 · 拖到左侧归类", Tokens.Label, Ui.Muted)); body.Children.Add(footer);
        var tiles = new WrapPanel(); foreach (var file in visible) tiles.Children.Add(BuildFileTile(file, boardSelection));
        if (selected.Count == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(20) };
            var symbol = Ui.Text("○", 42, Tokens.Primary600Brush); symbol.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(symbol);
            var title = Ui.Text(search.Text.Length > 0 ? "没有找到匹配文件" : "留一点空白，也很好", Tokens.SectionTitle, bold: true); title.Margin = new(0, 12, 0, 8); empty.Children.Add(title);
            empty.Children.Add(Ui.Text(search.Text.Length > 0 ? "换个关键词，或选择其他分区。" : "新文件会自动出现，也可以拖入分区。", Tokens.Secondary, Ui.Muted)); body.Children.Add(empty);
        }
        else body.Children.Add(new ScrollViewer { Content = tiles });
        var surface = new Border { Child = body, Background = Brushes.White, BorderBrush = Tokens.Brush(Tokens.Line100), BorderThickness = new(1), CornerRadius = new(12) };
        var target = collections.FirstOrDefault(c => c.Id == activeCollection);
        if (target is { MappedPath: null, Recent: false }) EnableCollectionDrop(surface, target.Id);
        Grid.SetColumn(surface, 1); layout.Children.Add(surface); return layout;
    }

    private void EnableCollectionDrop(UIElement element, string id)
    {
        element.AllowDrop = true;
        element.DragOver += (_, e) => { e.Effects = DragData(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        element.Drop += (_, e) => { var paths = DragData(e); if (paths.Length > 0) Run(() => organizer.AssignMany(paths, id)); e.Handled = true; };
    }
    internal static string[] DragData(DragEventArgs e) => e.Data.GetData("Lume.VirtualFiles") as string[] ?? (e.Data.GetData("Lume.VirtualFile") is string path ? [path] : e.Data.GetData(DataFormats.FileDrop) as string[] ?? []);

    internal void HandleFileKeys(TileSelection scope, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || e.Handled) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A) { scope.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.A || e.Key == Key.Escape) { scope.Clear(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(scope); e.Handled = true; }
        if (e.Handled) status.Text = $"已选中 {scope.Model.Selected.Count} 项";
    }

    private void DeleteSelected(TileSelection scope)
    {
        var selected = scope.Model.Visible.Where(scope.Model.Selected.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = organizer.Files.Where(f => selected.Contains(f.Path)).ToList();
        if (files.Count == 0) return;
        var names = string.Join("、", files.Take(3).Select(FilePresentation.DisplayName));
        // Smoke verification never deletes real user files; its fixtures are isolated.
        if (!smoke && MessageBox.Show(this, $"将 {files.Count} 个项目移入回收站？\n\n{names}\n\n可从 Windows 回收站恢复。", "移入回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            foreach (var file in files)
            {
                if (file.IsDirectory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(file.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally { scope.Clear(); _ = RefreshAsync(); }
    }

    internal ContextMenu FileMenu(DesktopFile file, TileSelection scope)
    {
        var menu = new ContextMenu(); var paths = scope.Model.PathsFor(file.Path);
        AddMenu(menu, "打开", () => OpenFile(file)); AddMenu(menu, "预览    空格", () => PreviewFile(file));
        var move = new MenuItem { Header = paths.Length > 1 ? $"将 {paths.Length} 项归入" : "归入分区" };
        foreach (var collection in organizer.State.Configuration.Collections.Where(c => c.MappedPath == null && !c.Recent))
        {
            var item = new MenuItem { Header = collection.Name }; item.Click += (_, _) => { Run(() => organizer.AssignMany(paths, collection.Id)); scope.Clear(); }; move.Items.Add(item);
        }
        menu.Items.Add(move); menu.Items.Add(new Separator());
        AddMenu(menu, "复制完整路径", () => Clipboard.SetText(string.Join(Environment.NewLine, paths)));
        AddMenu(menu, "在资源管理器中显示", () => ShellOpen("explorer.exe", "/select,\"" + file.Path + "\""));
        if (organizer.State.Configuration.Overrides.ContainsKey(file.Path)) AddMenu(menu, "恢复自动归类", () => Run(() => organizer.Release(file.Path)));
        menu.Items.Add(new Separator()); AddMenu(menu, paths.Length > 1 ? $"将 {paths.Length} 项移入回收站" : "移入回收站", () => DeleteSelected(scope));
        return menu;
    }

    internal UIElement BuildFileTile(DesktopFile file) => BuildFileTile(file, boardSelection);
    internal UIElement BuildFileTile(DesktopFile file, TileSelection scope)
    {
        var panel = new StackPanel { Width = 72 };
        panel.Children.Add(ShellIcons.CreateImage(file));
        var displayName = FilePresentation.DisplayName(file);
        var name = Ui.Text(displayName, Tokens.Label); name.TextAlignment = TextAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis; name.MaxHeight = 34; panel.Children.Add(name);
        var button = new Button { Content = panel, Width = 88, Height = 88, Padding = new(6), Margin = new(0, 0, 6, 8), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new(1), ToolTip = $"{displayName}\n{file.Path}\n{file.Source}\n双击打开；拖动或右键可归类" };
        Ui.UseStyle(button, "FileTileButton"); scope.Add(file, button);
        System.Windows.Automation.AutomationProperties.SetName(button, $"文件 {displayName}");
        button.MouseDoubleClick += (_, _) => OpenFile(file);
        Point start = default; var dragging = false; var preserveSelection = false;
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(button); dragging = false;
            preserveSelection = Keyboard.Modifiers == ModifierKeys.None && scope.Model.Selected.Count > 1 && scope.Model.Selected.Contains(file.Path);
            if (!preserveSelection) scope.Model.Select(file.Path, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            scope.Refresh(); status.Text = $"已选中 {scope.Model.Selected.Count} 项";
        };
        button.PreviewMouseLeftButtonUp += (_, _) => { if (preserveSelection && !dragging) { scope.Model.Select(file.Path); scope.Refresh(); } };
        button.PreviewMouseMove += (_, e) =>
        {
            var current = e.GetPosition(button);
            if (e.LeftButton != MouseButtonState.Pressed || Math.Abs(current.X - start.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - start.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
            dragging = true; var paths = scope.Model.PathsFor(file.Path);
            if (paths.Length == 1 && button.Tag is Func<Point, bool> localDrag && localDrag(current)) { e.Handled = true; return; }
            var data = new DataObject(); data.SetData("Lume.VirtualFile", file.Path); data.SetData("Lume.VirtualFiles", paths);
            DragDrop.DoDragDrop(button, data, DragDropEffects.Link | DragDropEffects.Move);
        };
        button.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenFile(file); e.Handled = true; } };
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) { PreviewFile(file); e.Handled = true; } };
        button.ContextMenu = new ContextMenu();
        button.ContextMenuOpening += (_, e) =>
        {
            if (!scope.Model.Selected.Contains(file.Path)) scope.Model.Select(file.Path);
            scope.Refresh();
            DesktopNative.GetCursorPos(out var pt);
            var window = Window.GetWindow(button);
            var hwnd = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            var paths = scope.Model.PathsFor(file.Path);
            var collections = organizer.State.Configuration.Collections.Where(c => c.MappedPath == null && !c.Recent).ToList();
            bool hasOverride = organizer.State.Configuration.Overrides.ContainsKey(file.Path);
            if (ShellContextMenu.Show(
                hwnd,
                paths,
                pt,
                onAssignToCollection: targetId => { Run(() => organizer.AssignMany(paths, targetId)); scope.Clear(); },
                onPreview: () => PreviewFile(file),
                onReleaseOverride: () => Run(() => organizer.Release(file.Path)),
                availableCollections: collections,
                hasOverride: hasOverride))
            {
                e.Handled = true;
            }
            else
            {
                button.ContextMenu = FileMenu(file, scope);
            }
        };
        return button;
    }
}
