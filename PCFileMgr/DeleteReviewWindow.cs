using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PCFileMgr.Core;

namespace PCFileMgr;

public sealed class DeleteReviewWindow : Window
{
    public DeleteReviewWindow(IReadOnlyList<FileNode> files, string scope)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "선택 파일 검토 · PCFileMgr"; Width = 840; Height = 580; MinWidth = 650; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new(26) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel { Margin = new(0, 0, 0, 18) };
        top.Children.Add(new TextBlock { Text = $"{files.Count:N0}개 파일을 휴지통으로 보낼까요?", FontSize = 24, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = $"총 {SizeFormat.Format(files.Sum(f => f.Size))} · {scope}", TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 8), Foreground = (Brush)FindResource("Muted") });
        top.Children.Add(new TextBlock { Text = "아래 파일만 처리합니다. 변경된 파일·연결 경로는 건너뛰고, 휴지통 이동이 불가능하면 실패로 표시합니다.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Accent") });
        grid.Children.Add(top);
        var list = new DataGrid { IsReadOnly = true, ItemsSource = files };
        list.Columns.Add(new DataGridTextColumn { Header = "전체 경로", Binding = new Binding("FullPath"), Width = new(1, DataGridLengthUnitType.Star) });
        list.Columns.Add(new DataGridTextColumn { Header = "크기", Binding = new Binding("SizeText"), Width = 110 });
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var bottom = new StackPanel { Margin = new(0, 18, 0, 0) }; Grid.SetRow(bottom, 2);
        var acknowledge = new CheckBox { Content = "목록을 확인했으며 선택한 파일을 정리합니다.", Margin = new(0, 0, 0, 18) };
        bottom.Children.Add(acknowledge);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 90, Margin = new(0, 0, 8, 0) };
        var confirm = new Button { Content = "휴지통으로 보내기", IsEnabled = false, Style = (Style)FindResource("PrimaryButton") };
        acknowledge.Checked += (_, _) => confirm.IsEnabled = true;
        acknowledge.Unchecked += (_, _) => confirm.IsEnabled = false;
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); bottom.Children.Add(buttons); grid.Children.Add(bottom);
        Content = grid;
    }
}
