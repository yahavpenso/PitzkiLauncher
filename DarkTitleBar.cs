using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

/// <summary>
/// Gives every window of the app a dark title bar, and dark drop-down lists and check boxes,
/// so nothing white sticks out of the dark design.
/// </summary>
static class DarkTitleBar
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    /// <summary>Rounds all four corners of the window (Windows 11 does it itself, Windows 10 gets a cut-out shape).</summary>
    static void Round(Window w, IntPtr hwnd)
    {
        int round = 2;
        if (DwmSetWindowAttribute(hwnd, 33, ref round, 4) == 0 && Environment.OSVersion.Version.Build >= 22000) return;
        void Cut()
        {
            var src = PresentationSource.FromVisual(w);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            int width = (int)Math.Round(w.ActualWidth * sx), height = (int)Math.Round(w.ActualHeight * sy), r = (int)Math.Round(16 * sx);
            if (w.WindowState == WindowState.Maximized) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, width + 1, height + 1, r, r), true);
        }
        Cut();
        w.SizeChanged += (_, _) => Cut();
        w.StateChanged += (_, _) => Cut();
    }

    [ModuleInitializer]
    internal static void Init()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));
    }

    static bool _styled;
    static readonly HashSet<Window> Barred = new();

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4); // dark mode (older Windows 10 used 19)
            int caption = 0x001C1615; // #15161C, the app background (Windows 11 only)
            DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            if (w.WindowStyle == WindowStyle.SingleBorderWindow) Round(w, hwnd);
        }
        OwnTitleBar(w);
        if (!_styled && Application.Current != null)
        {
            _styled = true;
            try { Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles)); } catch { }
        }
        Restyle(w);
    }

    /// <summary>
    /// A window may have its own plain style for check boxes, lists or menus (colours, margins). That would hide the
    /// dark look, so its setters are kept but the dark look is put underneath (BasedOn).
    /// </summary>
    static void Restyle(Window w)
    {
        if (Application.Current == null) return;
        foreach (var type in new[] { typeof(CheckBox), typeof(ComboBox), typeof(ComboBoxItem), typeof(ContextMenu), typeof(MenuItem), typeof(System.Windows.Controls.Primitives.ScrollBar) })
        {
            if (w.Resources[type] is not Style mine || Application.Current.Resources[type] is not Style dark || mine.BasedOn == dark) continue;
            var merged = new Style(type, dark);
            foreach (var s in mine.Setters)
                if (s is not Setter { Property.Name: "Template" }) merged.Setters.Add(s);
            foreach (var t in mine.Triggers) merged.Triggers.Add(t);
            w.Resources[type] = merged;
        }
    }

    /// <summary>
    /// Swaps the Windows title bar for Pitzki's own: the app icon and name on the left, and round
    /// minimize / maximize / close buttons on the right. Dragging, snapping and resizing still work.
    /// </summary>
    static void OwnTitleBar(Window w)
    {
        if (w.WindowStyle != WindowStyle.SingleBorderWindow || !Barred.Add(w) || w.Content is not UIElement body) return;
        const double h = 34;
        System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = h, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false,
        });
        var bg = new SolidColorBrush(Color.FromRgb(0x15, 0x16, 0x1C)); bg.Freeze();
        var bar = new DockPanel { Height = h, Background = bg, LastChildFill = true };

        Button Btn(string glyph, string tip, Color hot, Action click)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA4, 0xB8)) },
                Width = 46, Height = h, ToolTip = tip, Focusable = false, Cursor = Cursors.Hand,
                Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
  <Border x:Name="Bd" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
</ControlTemplate>
"""),
            };
            b.Loaded += (_, _) =>
            {
                if (b.Template.FindName("Bd", b) is not Border bd) return;
                var hotBrush = new SolidColorBrush(hot);
                b.MouseEnter += (_, _) => { bd.Background = hotBrush; ((TextBlock)b.Content).Foreground = Brushes.White; };
                b.MouseLeave += (_, _) => { bd.Background = Brushes.Transparent; ((TextBlock)b.Content).Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA4, 0xB8)); };
            };
            b.Click += (_, _) => click();
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }

        bar.Children.Add(Btn("", "Close", Color.FromRgb(0xE0, 0x45, 0x55), w.Close));
        if (w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            var max = Btn("", "Maximize", Color.FromRgb(0x2E, 0x31, 0x40), () => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            w.StateChanged += (_, _) => ((TextBlock)max.Content).Text = w.WindowState == WindowState.Maximized ? "" : "";
            bar.Children.Add(max);
        }
        if (w.ResizeMode != ResizeMode.NoResize)
            bar.Children.Add(Btn("", "Minimize", Color.FromRgb(0x2E, 0x31, 0x40), () => w.WindowState = WindowState.Minimized));

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        if (w.Icon != null) left.Children.Add(new Image { Source = w.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        var title = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCC, 0xDA)), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title") { Source = w });
        left.Children.Add(title);
        bar.Children.Add(left);

        w.Content = null;
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        // when maximized, Windows pushes the window a few pixels past the screen edge; keep the bar on screen
        w.StateChanged += (_, _) => root.Margin = w.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        w.Content = root;
    }

    const string Styles = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="PzField" Color="#232530"/>
  <SolidColorBrush x:Key="PzEdge" Color="#3A3D4C"/>
  <SolidColorBrush x:Key="PzHot" Color="#2E3140"/>

  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="#A0A4B8"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="Box" Width="16" Height="16" CornerRadius="4" Background="{StaticResource PzField}" BorderBrush="{StaticResource PzEdge}" BorderThickness="1" VerticalAlignment="Center">
              <Path x:Name="Mark" Data="M3,8 L6.5,11.5 L13,4.5" Stroke="White" StrokeThickness="2" Visibility="Collapsed" Margin="-1,-1,0,0"/>
            </Border>
            <ContentPresenter Margin="7,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Box" Property="Background" Value="#7C5CFF"/>
              <Setter TargetName="Box" Property="BorderBrush" Value="#7C5CFF"/>
              <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Box" Property="BorderBrush" Value="#7C5CFF"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.5"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="White"/>
    <Setter Property="Padding" Value="8,4"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="Bd" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Bd" Property="Background" Value="{StaticResource PzHot}"/></Trigger>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Bd" Property="Background" Value="#3B2F73"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="White"/>
    <Setter Property="Background" Value="{StaticResource PzField}"/>
    <Setter Property="BorderBrush" Value="{StaticResource PzEdge}"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Padding" Value="8,4"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="Bd" Background="{Binding Background, RelativeSource={RelativeSource AncestorType=ComboBox}}" BorderBrush="{Binding BorderBrush, RelativeSource={RelativeSource AncestorType=ComboBox}}" BorderThickness="1" CornerRadius="6">
                    <Path Data="M0,0 L4,4 L8,0" Stroke="#A0A4B8" StrokeThickness="1.5" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="#7C5CFF"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin="{TemplateBinding Padding}" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                              VerticalAlignment="Center" HorizontalAlignment="Left" IsHitTestVisible="False"/>
            <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
              <Border Background="#1C1E27" BorderBrush="{StaticResource PzEdge}" BorderThickness="1" CornerRadius="6" Padding="3" Margin="0,3,0,0"
                      MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="ContextMenu">
    <Setter Property="Foreground" Value="White"/>
    <Setter Property="SnapsToDevicePixels" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <Border Background="#1C1E27" BorderBrush="{StaticResource PzEdge}" BorderThickness="1" CornerRadius="8" Padding="4">
            <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="Separator" x:Key="{x:Static MenuItem.SeparatorStyleKey}">
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Separator">
          <Border Height="1" Margin="8,4" Background="{StaticResource PzEdge}"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="MenuItem">
    <Setter Property="Foreground" Value="White"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Border x:Name="Bd" Background="Transparent" CornerRadius="5" Padding="12,7" MinWidth="170">
            <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Bd" Property="Background" Value="#3B2F73"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="PzThumb" TargetType="Thumb">
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Thumb">
          <Border x:Name="T" Background="#3A3D4C" CornerRadius="4"/>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="T" Property="Background" Value="#6B6F82"/></Trigger>
            <Trigger Property="IsDragging" Value="True"><Setter TargetName="T" Property="Background" Value="#7C5CFF"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="PzPage" TargetType="RepeatButton">
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
  </Style>

  <!-- thin dark scroll bars, no arrows -->
  <Style TargetType="ScrollBar">
    <Setter Property="Width" Value="8"/>
    <Setter Property="MinWidth" Value="8"/>
    <Setter Property="Margin" Value="2,0,0,0"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ScrollBar">
          <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource PzPage}" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource PzPage}" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton>
            <Track.Thumb><Thumb Style="{StaticResource PzThumb}"/></Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal">
        <Setter Property="Width" Value="Auto"/>
        <Setter Property="MinWidth" Value="0"/>
        <Setter Property="Height" Value="8"/>
        <Setter Property="MinHeight" Value="8"/>
        <Setter Property="Margin" Value="0,2,0,0"/>
        <Setter Property="Template">
          <Setter.Value>
            <ControlTemplate TargetType="ScrollBar">
              <Track x:Name="PART_Track" IsDirectionReversed="False">
                <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource PzPage}" Command="ScrollBar.PageLeftCommand"/></Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource PzPage}" Command="ScrollBar.PageRightCommand"/></Track.IncreaseRepeatButton>
                <Track.Thumb><Thumb Style="{StaticResource PzThumb}"/></Track.Thumb>
              </Track>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>
""";
}
