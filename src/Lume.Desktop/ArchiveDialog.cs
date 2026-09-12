using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Data;
using System.Text.Json;
using Lume.Core;
using Microsoft.Win32;

namespace Lume.Desktop;

internal sealed class ArchiveDialog : Window
{
    private readonly Organizer organizer;
    private readonly ArchiveService service;
    private readonly ComboBox collection = new() { Width = 180, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly TextBox target = new() { MinWidth = 300, IsReadOnly = true };
    private readonly TextBox age = new() { Text = "7", Width = 65 };
    private readonly CheckBox monthly = new() { Content = "按当前月份建立子目录", IsChecked = true };
    private readonly TextBlock status = Ui.Text("先生成预览，再确认移动。文件夹不参与物理归档。", Tokens.Secondary, Ui.Muted);
    private readonly DataGrid table = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 36, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, MinHeight = 240, BorderThickness = new Thickness(0) };
    private readonly Grid tableHost = new();
    private readonly TextBlock emptyState = Ui.Text("选择分区与天数后生成归档预览。", Tokens.Body, Ui.Muted);
    private readonly string preferencesPath;
    private readonly Button execute;
    private readonly Button preview;
    private ArchiveBatch? batch;
    private bool busy;
    public ArchiveDialog(Window owner, Organizer organizer, ArchiveService service, string? collectionId = null, string? preferencesDirectory = null)
    {
        Owner = owner; this.organizer = organizer; this.service = service;
        preferencesPath = Path.Combine(preferencesDirectory ?? AppContext.BaseDirectory, "archive-ui.json");
        Style = (Style)Application.Current.FindResource(typeof(Window)); Title = "物理归档 · 预览后移动"; Width = 1050; Height = 650; MinWidth = 900; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(24) };
        var options = new StackPanel(); DockPanel.SetDock(options, Dock.Top);
        options.Children.Add(Ui.Text("文件真的搬走，也能原路恢复", Tokens.DialogTitle, bold: true));
        var description = Ui.Text("归档会移动普通文件。每个文件先复制校验、记录日志，再移除源文件；同名跳过。", Tokens.Secondary, Ui.Muted); description.Margin = new Thickness(0, 8, 0, 16); options.Children.Add(description);
        collection.ItemsSource = organizer.State.Configuration.Collections;
        var saved = LoadPreferences(); collection.SelectedValue = collectionId ?? saved.CollectionId ?? "inbox"; age.Text = saved.AgeDays.ToString(); monthly.IsChecked = saved.Monthly;
        var optionGrid = Ui.FormGrid(); Ui.AddFormRow(optionGrid, "分区", "选择需要整理的普通分区。", collection, 0);
        var ageRow = Ui.Row(age, Ui.Text("天（0 为全部）", Tokens.Secondary, Ui.Muted)); Ui.AddFormRow(optionGrid, "时间范围", "只处理超过指定天数未修改的文件。", ageRow, 1);
        target.Text = saved.Target ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lume归档"); target.Width = 300;
        var choose = Ui.Button("选择目录", () => { var dialog = new OpenFolderDialog { Title = "选择物理归档目标目录" }; if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName; }); choose.Width = 88;
        var destination = Ui.Row(target, choose); Ui.AddFormRow(optionGrid, "目标目录", "归档文件将写入此目录。", destination, 2);
        Ui.AddFormRow(optionGrid, "目录结构", "按当前月份建立子目录，便于按月恢复和查找。", monthly, 3);
        options.Children.Add(optionGrid); layout.Children.Add(options);
        var footer = new StackPanel { Margin = new(0, 15, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); status.Margin = new(0, 0, 0, 12); footer.Children.Add(status);
        preview = Ui.Button("生成归档预览", async () => await PreviewAsync());
        execute = Ui.Button("确认移动预览中的文件", async () => await ExecuteAsync(), true); execute.IsEnabled = false;
        footer.Children.Add(Ui.Row(preview, execute, Ui.Button("关闭", Close))); layout.Children.Add(footer);
        var headerStyle = new Style(typeof(DataGridColumnHeader)); headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Muted)); headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, Tokens.Secondary)); headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, Tokens.MediumWeight)); headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Tokens.Brush(Tokens.Line100))); headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1))); headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 8))); table.Resources[typeof(DataGridColumnHeader)] = headerStyle;
        var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(Control.MinHeightProperty, 36.0)); rowStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Tokens.Brush(Tokens.Line100))); rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1))); var hover = new Trigger { Property = DataGridRow.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, Tokens.Brush(Tokens.Surface50))); rowStyle.Triggers.Add(hover); table.Resources[typeof(DataGridRow)] = rowStyle;
        var cellStyle = new Style(typeof(DataGridCell)); cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 8, 0))); cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center)); table.Resources[typeof(DataGridCell)] = cellStyle;
        Style CellText(string path)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding(path)));
            return style;
        }
        table.Columns.Add(new DataGridTextColumn { Header = "源文件", Binding = new Binding("Source"), ElementStyle = CellText("Source"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star), MinWidth = 180 });
        table.Columns.Add(new DataGridTextColumn { Header = "归档位置", Binding = new Binding("Destination"), ElementStyle = CellText("Destination"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star), MinWidth = 180 });
        table.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding("Message"), ElementStyle = CellText("Message"), Width = new DataGridLength(.9, DataGridLengthUnitType.Star), MinWidth = 140 });
        table.Margin = new Thickness(0, 12, 0, 0); tableHost.Children.Add(table); emptyState.TextAlignment = TextAlignment.Center; emptyState.VerticalAlignment = VerticalAlignment.Center; tableHost.Children.Add(emptyState); layout.Children.Add(tableHost); Content = layout;
        table.Visibility = Visibility.Collapsed;
        void Invalidate() { if (busy) return; batch = null; execute.IsEnabled = false; table.ItemsSource = null; table.Visibility = Visibility.Collapsed; emptyState.Visibility = Visibility.Visible; }
        collection.SelectionChanged += (_, _) => Invalidate(); target.TextChanged += (_, _) => Invalidate(); age.TextChanged += (_, _) => Invalidate(); monthly.Click += (_, _) => Invalidate();
        Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "正在完成当前文件操作，请稍候。"; } else SavePreferences(); };
    }
    private void Busy(bool value) { busy = value; preview.IsEnabled = !value; execute.IsEnabled = !value && batch?.Items.Any(i => i.Status == "Planned") == true; collection.IsEnabled = !value; age.IsEnabled = !value; monthly.IsEnabled = !value; }
    private sealed record ArchiveUiState(string? CollectionId = null, string? Target = null, int AgeDays = 7, bool Monthly = true);
    private ArchiveUiState LoadPreferences()
    {
        try { return File.Exists(preferencesPath) ? JsonSerializer.Deserialize<ArchiveUiState>(File.ReadAllText(preferencesPath)) ?? new() : new(); }
        catch { return new(); }
    }
    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
            File.WriteAllText(preferencesPath, JsonSerializer.Serialize(new ArchiveUiState(collection.SelectedValue as string, target.Text, int.TryParse(age.Text, out var days) ? days : 7, monthly.IsChecked == true)));
        }
        catch { }
    }
    private async Task PreviewAsync()
    {
        if (!int.TryParse(age.Text, out var days) || days < 0) { status.Text = "天数必须是非负整数。"; return; }
        Busy(true);
        try
        {
            var files = organizer.CollectionFiles((string)collection.SelectedValue).Where(f => !f.IsDirectory && (days == 0 || f.ModifiedUtc < DateTime.UtcNow.AddDays(-days))).ToList();
            status.Text = "正在读取并校验待归档文件…";
            batch = await service.PreviewAsync(files, organizer.MonitorRoots, target.Text, monthly.IsChecked == true);
            table.ItemsSource = batch.Items; table.Visibility = Visibility.Visible; emptyState.Visibility = Visibility.Collapsed; status.Text = $"可移动 {batch.Items.Count(i => i.Status == "Planned")} 项，跳过 {batch.Items.Count(i => i.Status == "Skipped")} 项。核对目标后点击确认。";
        }
        catch (Exception ex) { status.Text = ex.Message; batch = null; }
        finally { Busy(false); }
    }
    private async Task ExecuteAsync()
    {
        if (batch == null) return; Busy(true);
        try
        {
            await Task.Run(() => service.ExecuteAsync(batch)); table.Items.Refresh();
            status.Text = $"已归档 {batch.Items.Count(i => i.Status == "Archived")} 项。可在整理历史中恢复；跳过或失败项请查看状态。";
        }
        catch (Exception ex) { status.Text = "归档停止：" + ex.Message + "。日志和未完成文件均已保留。"; }
        finally { Busy(false); }
    }
#if VERIFICATION
    internal async Task VerifyAsync(string outputDirectory)
    {
        collection.SelectedValue = "work"; age.Text = "0"; monthly.IsChecked = false;
        target.Text = Path.Combine(outputDirectory, "archive-roundtrip");
        await PreviewAsync();
        if (batch == null || !batch.Items.Any(i => i.Status == "Planned")) throw new InvalidOperationException("归档界面未生成有效预览。");
        var paths = batch.Items.Where(i => i.Status == "Planned").Select(i => i.Source).ToList();
        await ExecuteAsync();
        if (batch.Items.Any(i => i.Status != "Archived")) throw new InvalidOperationException("归档界面执行失败：" + string.Join("; ", batch.Items.Select(i => i.Message)));
        if (paths.Any(File.Exists)) throw new InvalidOperationException("物理归档后源文件仍存在。");
        await Task.Run(() => service.RestoreAsync(batch.Id));
        if (paths.Any(p => !File.Exists(p))) throw new InvalidOperationException("归档界面测试未恢复所有文件。");
    }
#endif
}
