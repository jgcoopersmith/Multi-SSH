using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MultiSSH.Services;

namespace MultiSSH.Views;

/// <summary>
/// A movable container that wraps a <see cref="SessionView"/> with an optional
/// header (title + reconnect + close). The same pane instance is moved between
/// the tab host and the tile host, so it must be detached from its old parent
/// before being re-added.
/// </summary>
public class SessionPane : Border
{
    public SessionView Session { get; }

    private readonly Border _header;
    private readonly TextBlock _titleText;
    private readonly Ellipse _statusDot;
    private bool _active;

    public event Action<SessionPane>? CloseRequested;
    public event Action<SessionPane>? ReconnectRequested;
    public event Action<SessionPane>? Activated;
    /// <summary>Raised when the header is double-clicked — toggle maximize/restore.</summary>
    public event Action<SessionPane>? MaximizeToggleRequested;
    /// <summary>Raised when this pane's header is dragged onto another pane: (dragged, target, after).</summary>
    public event Action<SessionPane, SessionPane, bool>? ReorderRequested;

    /// <summary>DataObject format used when dragging a pane (header) or a tab to reorder.</summary>
    public const string PaneDragFormat = "MultiSSH.SessionPane";

    private Point _headerDragStart;

    public SessionPane(SessionView session)
    {
        Session = session;
        BorderThickness = new Thickness(2);
        SetResourceReference(BorderBrushProperty, ThemeManager.ButtonBorder);

        var dock = new DockPanel { LastChildFill = true };

        _statusDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(SessionView.DotColor(session.State)),
            ToolTip = "Connection status",
        };

        _titleText = new TextBlock
        {
            Text = session.TabTitle,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, ThemeManager.Text);

        var reconnectBtn = MakeHeaderButton("⟳", "Reconnect");
        reconnectBtn.Click += (_, _) => ReconnectRequested?.Invoke(this);
        var closeBtn = MakeHeaderButton("✕", "Close");
        closeBtn.Click += (_, _) => CloseRequested?.Invoke(this);

        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(reconnectBtn);
        btns.Children.Add(closeBtn);
        DockPanel.SetDock(btns, Dock.Right);

        // Left side of the header: status dot + title.
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_statusDot);
        left.Children.Add(_titleText);

        var headerGrid = new DockPanel();
        headerGrid.Children.Add(btns);
        headerGrid.Children.Add(left);

        _header = new Border
        {
            Height = 26,
            Child = headerGrid,
            ToolTip = "Double-click to maximize / restore",
        };
        _header.SetResourceReference(Border.BackgroundProperty, ThemeManager.HeaderBg);
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);
        dock.Children.Add(session);

        Child = dock;

        session.TitleChanged += _ => _titleText.Text = session.TabTitle;
        session.StateChanged += _ => UpdateStatus();
        // Double-click the header to enlarge/restore. (In the terminal body a
        // double-click selects a word and a triple-click selects the line.)
        _header.MouseLeftButtonDown += (_, e) =>
        {
            Activated?.Invoke(this);
            if (e.ClickCount == 2) MaximizeToggleRequested?.Invoke(this);
        };
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);

        // Drag the header onto another pane to reorder (tiled / layered views).
        _header.PreviewMouseLeftButtonDown += (_, e) => _headerDragStart = e.GetPosition(null);
        _header.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _headerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _headerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragDrop.DoDragDrop(this, new DataObject(PaneDragFormat, this), DragDropEffects.Move);
        };
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(PaneDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(PaneDragFormat) is SessionPane dragged && !ReferenceEquals(dragged, this))
            {
                bool after = e.GetPosition(this).X > ActualWidth / 2;
                ReorderRequested?.Invoke(dragged, this, after);
            }
            e.Handled = true;
        };

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _statusDot.Fill = new SolidColorBrush(SessionView.DotColor(Session.State));
        _statusDot.ToolTip = Session.StatusText;
        _titleText.ToolTip = Session.StatusText;
    }

    public bool HeaderVisible
    {
        get => _header.Visibility == Visibility.Visible;
        set
        {
            _header.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            BorderThickness = value ? new Thickness(2) : new Thickness(0);
        }
    }

    public string Title => Session.TabTitle;

    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            SetResourceReference(BorderBrushProperty, value ? ThemeManager.Accent : ThemeManager.ButtonBorder);
        }
    }

    private static Button MakeHeaderButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph,
            ToolTip = tip,
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
        };
        b.SetResourceReference(Control.ForegroundProperty, ThemeManager.Text);
        return b;
    }

    /// <summary>Remove this pane from whatever parent currently holds it.</summary>
    public void DetachFromParent()
    {
        switch (Parent)
        {
            case Panel panel: panel.Children.Remove(this); break;
            case ContentControl cc when ReferenceEquals(cc.Content, this): cc.Content = null; break;
            case Decorator dec when ReferenceEquals(dec.Child, this): dec.Child = null; break;
        }
    }
}
