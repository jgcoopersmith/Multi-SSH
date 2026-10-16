using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MultiSSH.Services;

namespace MultiSSH.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        PopulateFonts();

        var s = AppSettings.Current;
        FontCombo.Text = s.DefaultFontFamily;
        FontSizeBox.Text = s.DefaultFontSize.ToString("0.#");
        SelectScheme(s.DefaultColorScheme);
        RecordFolderBox.Text = s.RecordingsFolder;

        KeepAliveBox.Text = s.DefaultKeepAliveSeconds.ToString();
        ChkNoDelay.IsChecked = s.DefaultTcpNoDelay;
        // ChkSoKeepalive stays checked: dead-connection keepalives are always on (RemoteAuth).
        (s.DefaultIpVersion switch
        {
            "IPv4" => RbIpv4,
            "IPv6" => RbIpv6,
            _ => RbIpAuto,
        }).IsChecked = true;

        ColsBox.Text = s.DefaultColumns.ToString();
        RowsBox.Text = s.DefaultRows.ToString();
        ScrollbackBox.Text = s.DefaultScrollbackLines.ToString();
        (s.ResizeBehavior switch
        {
            "FontSize" => RbFontSize,
            "FontSizeMax" => RbFontSizeMax,
            "Forbid" => RbForbid,
            _ => RbRowsCols,
        }).IsChecked = true;
        ChkScrollbar.IsChecked = s.DisplayScrollbar;
        ChkScrollbarFs.IsChecked = s.ScrollbarInFullScreen;
        ChkResetKeypress.IsChecked = s.ResetScrollbackOnKeypress;
        ChkResetActivity.IsChecked = s.ResetScrollbackOnActivity;
        ChkPushErased.IsChecked = s.PushErasedToScrollback;
        ChkWindowCounter.IsChecked = s.ShowWindowCounter;

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Multi-SSH version {ver?.ToString(3) ?? "1.0"}";

        UpdatePreview();
    }

    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select recordings folder" };
        var current = string.IsNullOrWhiteSpace(RecordFolderBox.Text)
            ? AppSettings.Current.EffectiveRecordingsFolder : RecordFolderBox.Text;
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true)
            RecordFolderBox.Text = dlg.FolderName;
    }

    private void PopulateFonts()
    {
        // Monospaced fonts first (best for a terminal), then everything else.
        var preferred = new[] { "Consolas", "Cascadia Mono", "Cascadia Code", "Lucida Console", "Courier New" };
        foreach (var f in preferred)
            FontCombo.Items.Add(f);
        FontCombo.Items.Add(new Separator());
        foreach (var fam in Fonts.SystemFontFamilies
                     .Select(f => f.Source)
                     .Where(n => !preferred.Contains(n))
                     .OrderBy(n => n))
            FontCombo.Items.Add(fam);
    }

    private void SelectScheme(string name)
    {
        foreach (ComboBoxItem item in SchemeCombo.Items)
            if ((string)item.Content == name) { SchemeCombo.SelectedItem = item; return; }
        SchemeCombo.SelectedIndex = 0;
    }

    private void Font_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (Preview == null) return;
        try
        {
            Preview.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(FontCombo.Text) ? "Consolas" : FontCombo.Text);
            if (double.TryParse(FontSizeBox.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var size) && size > 0)
                Preview.FontSize = size;
        }
        catch { /* invalid font name while typing */ }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Mutate the existing settings so hot keys, session folders, dock state, etc.
        // are preserved (never replace the whole object).
        var s = AppSettings.Current;
        s.DefaultFontFamily = string.IsNullOrWhiteSpace(FontCombo.Text) ? "Consolas" : FontCombo.Text.Trim();
        s.DefaultFontSize = double.TryParse(FontSizeBox.Text, out var sz) && sz > 0 ? sz : 14;
        s.DefaultColorScheme = (SchemeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Campbell";
        s.RecordingsFolder = RecordFolderBox.Text.Trim();

        s.DefaultKeepAliveSeconds = int.TryParse(KeepAliveBox.Text, out var ka) && ka >= 0 ? ka : 0;
        s.DefaultTcpNoDelay = ChkNoDelay.IsChecked == true;
        s.DefaultSoKeepalive = ChkSoKeepalive.IsChecked == true;
        s.DefaultIpVersion =
            RbIpv4.IsChecked == true ? "IPv4" :
            RbIpv6.IsChecked == true ? "IPv6" : "Auto";

        s.DefaultColumns = int.TryParse(ColsBox.Text, out var c) && c > 0 ? c : 80;
        s.DefaultRows = int.TryParse(RowsBox.Text, out var r) && r > 0 ? r : 24;
        s.DefaultScrollbackLines = int.TryParse(ScrollbackBox.Text, out var sb) && sb >= 0 ? sb : 2000;
        s.ResizeBehavior =
            RbFontSize.IsChecked == true ? "FontSize" :
            RbFontSizeMax.IsChecked == true ? "FontSizeMax" :
            RbForbid.IsChecked == true ? "Forbid" : "RowsCols";
        s.DisplayScrollbar = ChkScrollbar.IsChecked == true;
        s.ScrollbarInFullScreen = ChkScrollbarFs.IsChecked == true;
        s.ResetScrollbackOnKeypress = ChkResetKeypress.IsChecked == true;
        s.ResetScrollbackOnActivity = ChkResetActivity.IsChecked == true;
        s.PushErasedToScrollback = ChkPushErased.IsChecked == true;
        s.ShowWindowCounter = ChkWindowCounter.IsChecked == true;

        s.Save();
        DialogResult = true;
    }
}
