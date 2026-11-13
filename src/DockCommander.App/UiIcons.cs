using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace DockCommander.App;

// Small vector glyphs keep the UI crisp at any display scale, without an icon package.
internal static class UiIcons
{
    public static Control Create(string symbol, string? colorClass = null, double size = 16)
    {
        var geometry = symbol switch
        {
            "minimize" => "M4,11.5 L20,11.5 L20,12.5 L4,12.5 Z",
            "maximize" => "M4,4 L20,4 L20,20 L4,20 Z",
            "restore" => "M8,4 L20,4 L20,16 M4,8 L16,8 L16,20 L4,20 Z",
            "close" => "M5,5 L19,19 M19,5 L5,19",
            "folder" => "M2,6 L2,17 Q2,19 4,19 L20,19 Q22,19 22,17 L22,8 Q22,6 20,6 L11,6 L9,3 L4,3 Q2,3 2,5 Z",
            "up" => "M5,10 L12,3 L19,10 M12,3 L12,21",
            "drive" => "M5,4 L19,4 L22,14 L22,20 L2,20 L2,14 Z M2,14 L22,14 M17,17 L19,17",
            "sun" => "M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 M12,1 L12,3 M12,21 L12,23 M1,12 L3,12 M21,12 L23,12 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
            "moon" => "M20,15 A9,9 0 1 1 9,3 A7,7 0 0 0 20,15 Z",
            "copy" => "M8,8 L21,8 L21,21 L8,21 Z M16,5 L16,2 L2,2 L2,16 L5,16",
            "move" => "M3,12 L21,12 M15,6 L21,12 L15,18",
            "mkdir" => "M2,7 L2,19 L22,19 L22,7 L11,7 L8,4 L2,4 Z M12,10 L12,16 M9,13 L15,13",
            "trash" => "M3,6 L21,6 M9,3 L15,3 M5,6 L6,21 L18,21 L19,6 M10,10 L10,17 M14,10 L14,17",
            "view" => "M2,12 Q12,-2 22,12 Q12,26 2,12 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12",
            "edit" => "M4,16 L16,4 L20,8 L8,20 L3,21 Z M13,7 L17,11",
            "refresh" => "M20,8 A9,9 0 1 0 21,15 M20,2 L20,8 L14,8",
            "logo" => "M3,3 L10,3 L10,21 L3,21 Z M14,3 L21,3 L21,21 L14,21 Z",
            _ => "M5,2 L14,2 L20,8 L20,22 L5,22 Z M14,2 L14,8 L20,8 M8,13 L16,13 M8,17 L14,17"
        };
        var path = new Path { Data = Geometry.Parse(geometry), Stretch = Stretch.Uniform, Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        path.Classes.Add("glyph");
        if (symbol == "minimize") path.Height = 1;
        if (colorClass is not null) path.Classes.Add(colorClass);
        return path;
    }

    public static StackPanel Label(string symbol, string text, string? colorClass = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Create(symbol, colorClass));
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
}
