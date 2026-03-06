using WindowsNotesApp.Models;
using WindowsNotesApp.Services;

namespace WindowsNotesApp.Tests;

public class LocalizationServiceTests
{
    [TearDown]
    public void TearDown()
    {
        LocalizationService.ApplyLanguage(AppLanguage.English);
    }

    [Test]
    public void ApplyLanguage_UpdatesFrenchStrings()
    {
        LocalizationService.ApplyLanguage(AppLanguage.French);

        Assert.That(LocalizationService.Get("Main.Settings"), Is.EqualTo("Paramètres"));
        Assert.That(LocalizationService.CurrentCulture.Name, Is.EqualTo("fr-FR"));
    }

    [Test]
    public void Format_UsesChinesePageLabel()
    {
        LocalizationService.ApplyLanguage(AppLanguage.Chinese);

        Assert.That(LocalizationService.Format("Home.Info.Pages", 3), Is.EqualTo("3 页"));
    }
}
