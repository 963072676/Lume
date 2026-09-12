using Lume.Core;
internal static class SelectionTests
{
    internal static void Register(Action<string, Action> test)
    {
        void Check(bool ok) { if (!ok) throw new Exception("选择范围断言失败"); }
        test("全选只选当前筛选视图且路径大小写去重", () =>
        {
            var selection = new FileSelection(); selection.SetVisible(["B", "A", "b", "C"]); selection.SelectAll();
            Check(selection.Selected.Count == 3); selection.SetVisible(["B"]); Check(selection.Selected.SetEquals(["B"]));
            selection.Select("不可见文件"); Check(selection.Selected.SetEquals(["B"]));
        });
        test("Shift 遵循视觉顺序并保持锚点可缩小范围", () =>
        {
            var selection = new FileSelection(); selection.SetVisible(["C", "A", "D", "B"]); selection.Select("A");
            selection.Select("B", shift: true); Check(selection.Selected.SetEquals(["A", "D", "B"]));
            selection.Select("D", shift: true); Check(selection.Selected.SetEquals(["A", "D"]));
            Check(selection.PathsFor("A").SequenceEqual(["A", "D"]));
        });
        test("桌面分区和设置视图选择互不污染且换页清理旧选择", () =>
        {
            var desktop = new FileSelection(); var settings = new FileSelection(); desktop.SetVisible(["A", "B"]); settings.SetVisible(["C"]);
            desktop.SelectAll(); settings.SelectAll(); Check(desktop.Selected.SetEquals(["A", "B"]));
            settings.Clear(); Check(desktop.Selected.Count == 2); desktop.SetVisible(["D", "E"]); Check(desktop.Selected.Count == 0);
        });
        test("Ctrl 点击可切换选中且组合范围保留已选项", () =>
        {
            var selection = new FileSelection(); selection.SetVisible(["A", "B", "C", "D"]); selection.Select("A"); selection.Select("C", control: true);
            selection.Select("D", control: true, shift: true); Check(selection.Selected.SetEquals(["A", "C", "D"]));
            selection.Select("C", control: true); Check(selection.Selected.SetEquals(["A", "D"]));
        });
    }
}
