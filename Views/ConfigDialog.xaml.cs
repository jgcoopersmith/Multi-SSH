using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MultiSSH.Models;
using MultiSSH.Services;

namespace MultiSSH.Views;

public partial class ConfigDialog : Window
{
    public SessionConfig Result { get; private set; } = new();
    public bool SaveToSidebar => SaveChk.IsChecked == true;

    private readonly StackPanel[] _panels;

    public ConfigDialog(SessionConfig? existing = null, string? defaultFolder = null)
    {
        InitializeComponent();
        _panels = new[]
        {
            PanelSession, PanelConnection, PanelAuth, PanelTerminal, PanelAppearance, PanelBehaviour
        };
        PopulateFolders();
        Load(existing ?? NewSessionFromDefaults(defaultFolder));
    }

    private void PopulateFolders()
    {
        FolderCombo.Items.Clear();
        FolderCombo.Items.Add("");   // top level
        foreach (var f in AppSettings.Current.SessionFolders
                     .OrderBy(x => x, System.StringComparer.OrdinalIgnoreCase))
            FolderCombo.Items.Add(f);
    }

    /// <summary>A blank session pre-filled with the app-wide defaults (font, scheme).</summary>
    private static SessionConfig NewSessionFromDefaults(string? defaultFolder = null)
    {
        var app = AppSettings.Current;
        return new SessionConfig
        {
            FontFamily = app.DefaultFontFamily,
            FontSize = app.DefaultFontSize,
            ColorScheme = app.DefaultColorScheme,
            FolderPath = defaultFolder ?? "",
            Columns = app.DefaultColumns,
            Rows = app.DefaultRows,
            ScrollbackLines = app.DefaultScrollbackLines,
            KeepAliveSeconds = app.DefaultKeepAliveSeconds,
            TcpNoDelay = app.DefaultTcpNoDelay,
            SoKeepalive = app.DefaultSoKeepalive,
            IpVersion = app.DefaultIpVersion,
        };
    }

    private void Load(SessionConfig c)
    {
        KindCombo.SelectedIndex = (int)c.Kind;
        HostBox.Text = c.Host;
        PortBox.Text = c.Port.ToString();
        NameBox.Text = c.Name;
        FolderCombo.Text = c.FolderPath ?? "";
        UserBox.Text = c.Username;
        KeepAliveBox.Text = c.KeepAliveSeconds.ToString();
        TimeoutBox.Text = c.ConnectTimeoutSeconds.ToString();
        NoDelayChk.IsChecked = c.TcpNoDelay;
        SoKeepaliveChk.IsChecked = c.SoKeepalive;
        (c.IpVersion switch
        {
            "IPv4" => RbIpv4,
            "IPv6" => RbIpv6,
            _ => RbIpAuto,
        }).IsChecked = true;
        AuthCombo.SelectedIndex = (int)c.Auth;
        PassBox.Password = c.Password ?? "";
        KeyPathBox.Text = c.PrivateKeyPath ?? "";
        PassphraseBox.Password = c.KeyPassphrase ?? "";
        TermTypeBox.Text = c.TerminalType;
        ColsBox.Text = c.Columns.ToString();
        RowsBox.Text = c.Rows.ToString();
        ScrollbackBox.Text = c.ScrollbackLines.ToString();
        FontCombo.Text = c.FontFamily;
        FontSizeBox.Text = c.FontSize.ToString("0.#");
        SelectComboByContent(SchemeCombo, c.ColorScheme);
        BellChk.IsChecked = c.BellEnabled;
        CopySelChk.IsChecked = c.CopyOnSelect;
        RightPasteChk.IsChecked = c.PasteOnRightClick;
        UpdateAuthFieldStates();
        UpdateKindFieldStates();
    }

    private SessionKind SelectedKind => (SessionKind)Math.Max(0, KindCombo.SelectedIndex);

    private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateKindFieldStates();

    /// <summary>The type icons are a shortcut for the Session type dropdown.</summary>
    private void KindIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag as string, out var index))
            KindCombo.SelectedIndex = index;
    }

    /// <summary>Outline the icon matching the selected type.</summary>
    private void HighlightKindIcon()
    {
        var on = new SolidColorBrush(Color.FromRgb(0x1B, 0x6A, 0xC6));
        var off = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
        var icons = new[] { IconSsh, IconPs, IconCmd, IconBash, IconWsl, IconSftp, IconScp, IconWebdav, IconWscp };
        for (int i = 0; i < icons.Length; i++)
        {
            bool active = i == (int)SelectedKind;
            icons[i].BorderBrush = active ? on : off;
            icons[i].BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    /// <summary>
    /// A local shell has no host, port, credentials or connection tuning, so
    /// grey those out rather than pretending they apply.
    /// </summary>
    private void UpdateKindFieldStates()
    {
        if (HostBox == null) return;   // still initialising
        bool remote = !SessionConfig.IsLocalKind(SelectedKind);

        HostBox.IsEnabled = PortBox.IsEnabled = remote;
        HostLabel.IsEnabled = PortLabel.IsEnabled = remote;
        PanelConnection.IsEnabled = PanelAuth.IsEnabled = remote;

        HighlightKindIcon();

        KindHint.Text = SelectedKind switch
        {
            SessionKind.Wscp => "Connection type: WSCP — a WinSCP-style dual-pane file manager over SFTP (uses the SSH host/port/login).",
            SessionKind.WebDav => "Connection type: WebDAV — put the server host/URL in Host and the port (443 or 80).",
            var k when SessionConfig.IsLocalKind(k) =>
                $"Connection type: local {SessionConfig.KindName(k)} — runs on this PC, no host or login needed.",
            _ => $"Connection type: {SessionConfig.KindName(SelectedKind)} (port {PortBox.Text}).",
        };
    }

    private static void SelectComboByContent(ComboBox combo, string content)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Content == content) { combo.SelectedItem = item; return; }
        }
        combo.SelectedIndex = 0;
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = CategoryList.SelectedIndex;
        if (_panels == null) return;
        for (int i = 0; i < _panels.Length; i++)
            _panels[i].Visibility = (i == idx) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateAuthFieldStates();

    private void UpdateAuthFieldStates()
    {
        // Keep every auth field usable regardless of the selected method, so a
        // key file, passphrase, or password can always be entered or browsed to.
        if (PassBox == null) return;
        KeyPathBox.IsEnabled = BrowseKeyBtn.IsEnabled = PassphraseBox.IsEnabled = true;
        PassBox.IsEnabled = true;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select private key",
            Filter = "Private keys (*.pem;*.key;*.ppk;id_*)|*.pem;*.key;*.ppk;id_*|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            KeyPathBox.Text = dlg.FileName;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var kind = SelectedKind;
        // Every remote kind needs a host — the caller silently discards one saved without it.
        if (!SessionConfig.IsLocalKind(kind) && string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show(this, "Please enter a host name or IP address.", "Multi-SSH",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var c = new SessionConfig
        {
            Kind = kind,
            Host = HostBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Username = UserBox.Text.Trim(),
            FolderPath = (FolderCombo.Text ?? "").Trim().Trim('/'),
            Auth = (AuthMethod)Math.Max(0, AuthCombo.SelectedIndex),
            Password = string.IsNullOrEmpty(PassBox.Password) ? null : PassBox.Password,
            PrivateKeyPath = string.IsNullOrWhiteSpace(KeyPathBox.Text) ? null : KeyPathBox.Text.Trim(),
            KeyPassphrase = string.IsNullOrEmpty(PassphraseBox.Password) ? null : PassphraseBox.Password,
            TerminalType = string.IsNullOrWhiteSpace(TermTypeBox.Text) ? "xterm-256color" : TermTypeBox.Text.Trim(),
            FontFamily = string.IsNullOrWhiteSpace(FontCombo.Text) ? "Consolas" : FontCombo.Text.Trim(),
            ColorScheme = (SchemeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Campbell",
            BellEnabled = BellChk.IsChecked == true,
            CopyOnSelect = CopySelChk.IsChecked == true,
            PasteOnRightClick = RightPasteChk.IsChecked == true,
            TcpNoDelay = NoDelayChk.IsChecked == true,
            SoKeepalive = SoKeepaliveChk.IsChecked == true,
            IpVersion =
                RbIpv4.IsChecked == true ? "IPv4" :
                RbIpv6.IsChecked == true ? "IPv6" : "Auto",
        };

        c.Port = ParseInt(PortBox.Text, 22);
        c.KeepAliveSeconds = ParseInt(KeepAliveBox.Text, 0);
        c.ConnectTimeoutSeconds = ParseInt(TimeoutBox.Text, 15);
        c.Columns = ParseInt(ColsBox.Text, 80);
        c.Rows = ParseInt(RowsBox.Text, 24);
        c.ScrollbackLines = ParseInt(ScrollbackBox.Text, 2000);
        c.FontSize = ParseDouble(FontSizeBox.Text, 14);

        // A key file means public-key auth — use the key and never prompt for a password.
        if (!string.IsNullOrWhiteSpace(c.PrivateKeyPath))
            c.Auth = AuthMethod.PublicKey;

        Result = c;
        DialogResult = true;
    }

    private static int ParseInt(string s, int fallback) => int.TryParse(s, out var v) ? v : fallback;
    private static double ParseDouble(string s, double fallback) => double.TryParse(s, out var v) ? v : fallback;
}
