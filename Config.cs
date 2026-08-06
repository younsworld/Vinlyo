using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vinlyo;

/// <summary>
/// Etat persistant du widget : position a l'ecran et preferences du menu
/// contextuel. Serialise en JSON dans %APPDATA%\Vinlyo\config.json.
/// </summary>
public sealed partial class Config
{
    // Les proprietes sont publiques et modifiables : System.Text.Json a besoin
    // d'un setter public pour deserialiser sans configuration supplementaire.
    // Left/Top sont nullables plutot que NaN : System.Text.Json refuse d'ecrire
    // NaN, et "pas encore positionne" se represente naturellement par null.
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Locked { get; set; }

    /// <summary>Diametre affiche du disque, en pixels. Le trace vectoriel est
    /// defini une fois pour toutes a 220 : c'est une mise a l'echelle.</summary>
    public int DiscSize { get; set; } = 220;

    /// <summary>Teinter le disque avec la couleur dominante de la pochette.</summary>
    public bool Tint { get; set; } = true;

    /// <summary>Intensite de la teinte, de 0 (noir) a 1 (saturation maximale).</summary>
    public double TintStrength { get; set; } = 0.6;

    /// <summary>Caler la vitesse de rotation sur le tempo detecte.</summary>
    public bool BpmSync { get; set; }

    /// <summary>Duree d'un tour en mode vitesse fixe, en secondes.</summary>
    public double SecondsPerTurn { get; set; } = 6;

    /// <summary>Nombre de temps par tour en mode BPM. 4 = une mesure.</summary>
    public int BeatsPerTurn { get; set; } = 4;

    /// <summary>Secondes de piste parcourues par un tour complet de scratch.</summary>
    public double ScratchSecondsPerTurn { get; set; } = 30;

    /// <summary>Afficher le titre et l'artiste a cote du disque.</summary>
    public bool ShowTrackInfo { get; set; } = true;

    /// <summary>Vrai : le texte se place sous le disque. Faux : a sa droite.</summary>
    public bool TrackInfoBelow { get; set; } = true;

    /// <summary>%APPDATA%\Vinlyo</summary>
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Vinlyo");

    private static string FilePath => Path.Combine(Folder, "config.json");

    /// <summary>
    /// Contexte de serialisation source-genere : evite la reflexion a
    /// l'execution, ce qui accelere le demarrage et allege la memoire.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Config))]
    private partial class JsonContext : JsonSerializerContext { }

    /// <summary>
    /// Relit la configuration. Toute erreur (fichier absent, JSON corrompu,
    /// droits refuses) redonne une configuration par defaut : le widget doit
    /// demarrer quoi qu'il arrive.
    /// </summary>
    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize(json, JsonContext.Default.Config);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // Configuration illisible : on repart sur les valeurs par defaut.
        }

        return new Config();
    }

    /// <summary>
    /// Ecrit la configuration. Silencieuse en cas d'echec : ne pas perdre une
    /// position vaut mieux que faire planter le widget.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonContext.Default.Config));
        }
        catch
        {
            // Disque plein ou droits refuses : rien a faire de plus ici.
        }
    }
}

/// <summary>
/// Gestion du lancement au demarrage de Windows. On passe par un raccourci
/// dans le dossier Demarrage de l'utilisateur, jamais par le registre.
/// </summary>
public static class StartupShortcut
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Vinlyo.lnk");

    public static bool IsEnabled => File.Exists(ShortcutPath);

    /// <summary>
    /// Cree ou supprime le raccourci. La creation utilise WScript.Shell en
    /// liaison tardive (COM via reflexion) : c'est ce qui permet d'ecrire un
    /// vrai .lnk sans la moindre dependance NuGet.
    /// </summary>
    public static void Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            try
            {
                // shell.CreateShortcut(path) renvoie un objet COM IWshShortcut
                // dont on renseigne les proprietes avant d'appeler Save().
                var link = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                if (link is null) return;

                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, link, new object[] { exe });
                linkType.InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty, null, link,
                    new object[] { Path.GetDirectoryName(exe) ?? string.Empty });
                linkType.InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "Vinlyo" });
                linkType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                // Liberation explicite de l'objet COM.
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            // Dossier Demarrage inaccessible ou COM indisponible : on ignore,
            // l'option restera simplement sans effet.
        }
    }
}
