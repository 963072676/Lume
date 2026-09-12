using System.Windows.Controls;
using Lume.Core;
namespace Lume.Desktop;

internal sealed class TileSelection
{
    public FileSelection Model { get; } = new();
    private readonly List<(string Path, Button Button)> buttons = [];
    public void SetFiles(IEnumerable<DesktopFile> files) { Model.SetVisible(files.Select(f => f.Path)); buttons.Clear(); }
    public void Add(DesktopFile file, Button button) { buttons.Add((file.Path, button)); Ui.UpdateTile(button, Model.Selected.Contains(file.Path)); }
    public void Refresh() { foreach (var (path, button) in buttons) Ui.UpdateTile(button, Model.Selected.Contains(path)); }
    public void Clear() { Model.Clear(); Refresh(); }
    public void SelectAll() { Model.SelectAll(); Refresh(); }
}
