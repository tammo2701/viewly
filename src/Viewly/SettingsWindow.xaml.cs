using System.IO;
using System.Text.Json;
using System.Windows;

namespace Viewly;

public partial class SettingsWindow : Window
{
    private readonly string _settingsPath;
    public SettingsWindow(string settingsPath, string currentToken)
    {
        InitializeComponent();
        _settingsPath = settingsPath;
        TokenBox.Password = currentToken;
    }

    public string SavedToken { get; private set; } = "";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SavedToken = TokenBox.Password.Trim();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temp = _settingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ViewlySettings(SavedToken), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _settingsPath, true);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Die Einstellung konnte nicht auf dem Stick gespeichert werden.\n\n" + ex.Message, "Viewly", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
