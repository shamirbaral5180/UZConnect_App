using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UZConnect.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}

public partial class MessagesView : UserControl
{
    public MessagesView() => InitializeComponent();

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.MessagesViewModel vm &&
            sender is ListView list)
        {
            vm.SetSelectedRows(list.SelectedItems.OfType<ViewModels.MessageRow>());
        }
    }

    private void MessageList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A plain click on a row opens the classic Edit dialog. Checkbox clicks
        // and Ctrl/Shift multi-selection are left alone.
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is
                { DataContext: ViewModels.MessageRow row } &&
            DataContext is ViewModels.MessagesViewModel vm)
        {
            vm.OpenMessage(row);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async void SendToButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MessagesViewModel vm) return;
        var contacts = await vm.GetContactsForComposeAsync();
        if (contacts.Count == 0)
        {
            System.Windows.MessageBox.Show("No SIM contacts are available.", "UZConnect",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var dialog = new System.Windows.Window
        {
            Title = "Send to",
            Width = 340,
            Height = 380,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Window.GetWindow(this),
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        };
        var list = new System.Windows.Controls.ListBox { Margin = new System.Windows.Thickness(8), DisplayMemberPath = "Number" };
        foreach (var contact in contacts) list.Items.Add(contact);
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 76, Margin = new System.Windows.Thickness(4), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 76, Margin = new System.Windows.Thickness(4), IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(8) };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        dialog.Content = panel;
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true && list.SelectedItem is UZConnect.Core.Models.SimContact selected)
        {
            vm.ComposeTo = selected.Number;
        }
    }
}

public partial class ContactsView : UserControl
{
    public ContactsView() => InitializeComponent();
}

public partial class NetworkView : UserControl
{
    public NetworkView() => InitializeComponent();
}

public partial class ConnectionView : UserControl
{
    public ConnectionView() => InitializeComponent();
}

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void ProfilePassword_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm && sender is System.Windows.Controls.PasswordBox box)
        {
            vm.ProfilePassword = box.Password;
        }
    }

    private void ProfileSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm &&
            sender is System.Windows.Controls.ComboBox combo &&
            combo.SelectedItem is ViewModels.ApnProfileRow row)
        {
            vm.SelectedProfile = row;
        }
    }
}


public partial class UssdView : UserControl
{
    public UssdView() => InitializeComponent();
}

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();
}

public partial class HelpView : UserControl
{
    public HelpView() => InitializeComponent();
}

