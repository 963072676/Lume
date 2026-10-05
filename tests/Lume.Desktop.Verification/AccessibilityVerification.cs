using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Lume.Core;

namespace Lume.Desktop;

internal static class AccessibilityVerification
{
    internal static int Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "accessibility-verification"); Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var windows = new List<Window>(); var exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InstallStyles(app);
        app.Startup += async (_, _) =>
        {
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
                AutomationPeer Peer(UIElement input) => UIElementAutomationPeer.CreatePeerForElement(input) ?? throw new InvalidOperationException("缺少自动化属性");
                T Input<T>(object owner, string name) where T : UIElement => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
                T Keep<T>(T window) where T : Window { window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -16000; window.Top = 0; windows.Add(window); return window; }
                var anchor = Ui.Button("键盘菜单定位", () => { }); anchor.Width = 100; anchor.Height = 40;
                var host = Ui.Row(anchor); var owner = Keep(new Window { Content = host, Width = 300, Height = 120 }); owner.Show(); await Task.Delay(40); owner.UpdateLayout();
                Check(!owner.ShowActivated && !owner.ShowInTaskbar, "隔离窗口不激活、不显示任务栏、不请求焦点");
                var input = new TextBox(); var form = Ui.FormGrid();
                Ui.AddFormRow(form, "时间范围", "单位为天", Ui.Row(input, Ui.Text("天")), 0);
                Check(Peer(input).GetName() == "时间范围" && Peer(input).GetHelpText() == "单位为天", "表单行中的嵌套输入关联字段标签和说明");
                var caption = (TextBlock)AutomationProperties.GetLabeledBy(input); caption.Text = "等待天数";
                Check(Peer(input).GetName() == "等待天数", "字段标签更新后辅助名称同步更新");
                var explicitName = new TextBox(); AutomationProperties.SetName(explicitName, "已配置的字段名"); AutomationProperties.SetHelpText(explicitName, "已有帮助");
                var oldLabel = Ui.Text("已有标签"); AutomationProperties.SetLabeledBy(explicitName, oldLabel);
                Ui.LabelInput(explicitName, Ui.Text("新标签"), "新帮助");
                Check(Peer(explicitName).GetName() == "已配置的字段名" && Peer(explicitName).GetHelpText() == "已有帮助" && ReferenceEquals(AutomationProperties.GetLabeledBy(explicitName), oldLabel), "表单关联保留明确设置的名称、标签和帮助");
                var labelOnly = new TextBox(); AutomationProperties.SetLabeledBy(labelOnly, oldLabel); Ui.LabelInput(labelOnly, Ui.Text("其他标签"));
                Check(Peer(labelOnly).GetName() == "已有标签", "已有标签未显式设置名称时仍保持原字段含义");
                var tooltip = new TextBox { ToolTip = "已有提示" }; Ui.LabelInput(tooltip, Ui.Text("字段"), "其他说明");
                Check(Peer(tooltip).GetHelpText() == "已有提示", "表单说明不覆盖既有输入提示");
                var button = Ui.Button("选择目录", () => { }); var check = new CheckBox { Content = "按月建立子目录" };
                Ui.LabelInput(Ui.Row(button, check), Ui.Text("目录结构"));
                Check(Peer(button).GetName() == "选择目录" && Peer(check).GetName() == "按月建立子目录", "关联输入标签不改变按钮与复选框自身含义");
                var store = new StateStore(Path.Combine(fixture, "state.json")); var organizer = new Organizer(store, AppState.Create([]));
                var archive = Keep(new ArchiveDialog(owner, organizer, new ArchiveService(Path.Combine(fixture, "journals")), preferencesDirectory: fixture));
                foreach (var (field, name) in new[] { ("collection", "分区"), ("age", "时间范围"), ("target", "目标目录") })
                    Check(Peer(Input<Control>(archive, field)).GetName() == name, "归档输入提供字段名称：" + name);
                var rule = Keep(new RuleDialog(owner, organizer, aiDirectory: fixture));
                Check(Peer(Input<TextBox>(rule, "name")).GetName() == "规则名称", "规则名称输入可识别");
                Check(Peer(Input<TextBox>(rule, "aiPrompt")).GetName().Contains("AI 生成条件"), "规则用途输入可识别");
                Check(Peer(Input<ComboBox>(rule, "target")).GetName().Contains("放入分区"), "规则目标分区可识别");
                var row = (StackPanel)Input<StackPanel>(rule, "rows").Children[0]; var fieldBox = (ComboBox)row.Children[0]; var op = (ComboBox)row.Children[1]; var value = (ContentControl)row.Children[2];
                Check(Peer(fieldBox).GetName() == "条件字段" && Peer(op).GetName() == "扩展名匹配方式" && Peer((UIElement)value.Content).GetName() == "扩展名条件值", "规则条件字段、方式和值含有用途名称");
                fieldBox.SelectedValue = "kind";
                Check(value.Content is ComboBox && Peer((UIElement)value.Content).GetName() == "文件 / 文件夹条件值" && Peer(op).GetName() == "文件 / 文件夹匹配方式", "条件改成类型选择时同步更新辅助名称");
                fieldBox.SelectedValue = "name";
                Check(value.Content is TextBox && Peer((UIElement)value.Content).GetName() == "文件名条件值", "条件切回文本时辅助名称保持正确");
                var ai = Keep(new AiAnalysisDialog(owner, organizer, fixture, () => { }));
                foreach (var (field, name) in new[] { ("baseUrl", "服务地址"), ("key", "API Key"), ("model", "模型"), ("preference", "归类偏好") })
                    Check(Peer(Input<Control>(ai, field)).GetName() == name, "AI 配置输入提供字段名称：" + name);
                Check(Peer(Input<PasswordBox>(ai, "key")).IsPassword(), "密钥输入保留密码控件语义");
                var main = Keep(new MainWindow(organizer, store, true, false)); var search = Input<TextBox>(main, "search");
                Check(Peer(search).GetName() == "搜索当前视图文件" && Peer(search).GetAcceleratorKey() == "Ctrl+F", "文件视图搜索保留用途名称并提供快捷键");
                var scope = new TileSelection(); var file = new DesktopFile(Path.Combine(fixture, "同名.txt"), "同名.txt", ".txt", 1, DateTime.UnixEpoch, DateTime.UnixEpoch, false, "验证");
                scope.SetFiles([file]); var tile = (Button)main.BuildFileTile(file, scope); var tilePeer = Peer(tile);
                Check(tile is FileTileButton && tilePeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider && tilePeer.GetName().Contains("同名") && tilePeer.GetHelpText() == file.Path && tilePeer.GetItemStatus() == "未选中", "文件图块提供执行入口、名称、完整路径和未选状态");
                scope.Model.Select(file.Path); scope.Refresh();
                Check(tilePeer.GetItemStatus() == "已选中", "文件图块选择后可读取当前选中状态");
                scope.Clear(); Check(tilePeer.GetItemStatus() == "未选中", "清除选择后辅助状态随之更新");
                var opened = 0; var invokable = new FileTileButton(() => opened++) { Content = "虚构文件", Width = 100, Height = 40 }; Ui.UseStyle(invokable, "FileTileButton"); host.Children.Add(invokable);
                await Task.Delay(30); owner.UpdateLayout(); var provider = (IInvokeProvider)Peer(invokable).GetPattern(PatternInterface.Invoke)!;
                provider.Invoke(); await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Check(opened == 1, "辅助执行调用文件打开回调，未实际打开文件");
                invokable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(opened == 1, "普通按钮单击事件不触发文件打开");
                invokable.IsEnabled = false; var rejected = false; try { provider.Invoke(); } catch (ElementNotEnabledException) { rejected = true; }
                Check(rejected && opened == 1, "禁用图块拒绝辅助执行");
                invokable.IsEnabled = true; provider.Invoke(); invokable.Visibility = Visibility.Collapsed; await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Check(opened == 1, "排队后隐藏的图块不执行过时的打开回调");
                rejected = false; try { provider.Invoke(); } catch (ElementNotAvailableException) { rejected = true; }
                Check(rejected && opened == 1, "不可见图块拒绝辅助执行");
                var card = Keep(new DesktopCardWindow(organizer, organizer.State.Configuration.Collections[0], new(-16000, 0), IntPtr.Zero, (_, _) => new Button(), () => { }, () => { }, _ => { }));
                Check(Peer(Input<TextBox>(card, "search")).GetName() == "搜索此分区的文件", "分区搜索输入提供用途名称");
                var point = Ui.ContextMenuPoint(anchor, true); var start = anchor.PointToScreen(new Point(0, 0)); var end = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
                Check(point.X < -10000 && point.X >= start.X && point.X <= end.X && point.Y >= start.Y && point.Y <= end.Y, "键盘菜单位置属于目标图块，使用屏幕坐标，不依赖鼠标位置");
                Result(true);
            }
            catch (Exception ex) { exit = 1; Result(false, ex.ToString()); }
            finally { foreach (var window in windows.AsEnumerable().Reverse()) window.Close(); app.Shutdown(); }
        };
        void Result(bool passed, string? error = null)
        {
            var result = JsonSerializer.Serialize(new { passed, checks, error, version=RuntimeIdentity.Version, commit=RuntimeIdentity.Commit, desktopTakeover=false, inputSimulation=false, focusRequested=false }, new JsonSerializerOptions { WriteIndented=true });
            File.WriteAllText(Path.Combine(fixture, "result.json"), result); File.WriteAllText(Path.Combine(root, "latest-result.json"), result);
        }
        app.Run(); return exit;
    }
}
