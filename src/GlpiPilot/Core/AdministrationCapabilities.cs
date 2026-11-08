namespace GlpiPilot.Core;

/// <summary>Selected at compilation; never inferred from a filename, flag or WebView argument.</summary>
public static class AdministrationCapabilities
{
#if GLPI_PILOT_ADMIN
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif
    public static void Require()
    {
        if (!Enabled) throw new InvalidOperationException("Cette commande nécessite GLPI-Pilot-Admin.exe. L’édition Standard ne dispose pas des fonctions d’administration.");
    }
}
