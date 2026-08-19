using System.Windows;
using DeskPet.Windows.Views;

namespace DeskPet.Windows;

public partial class App : System.Windows.Application
{
    public App()
    {
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new PetWindow().Show();
    }
}