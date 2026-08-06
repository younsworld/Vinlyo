using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Vinlyo;

/// <summary>
/// Enveloppe autour de SMTC (System Media Transport Controls). Fournit au
/// widget trois informations : y a-t-il une session, joue-t-elle, et quelle est
/// la pochette du morceau courant.
///
/// Tout est evenementiel : aucun timer, aucune scrutation. SMTC previent
/// lui-meme quand la session change, quand les metadonnees changent et quand
/// l'etat de lecture change.
///
/// Les callbacks WinRT arrivent sur un thread du pool. Chaque notification
/// vers l'exterieur est donc reexpediee sur le Dispatcher de l'interface.
/// </summary>
public sealed class MediaSession : IDisposable
{
    private readonly Dispatcher _dispatcher;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>
    /// Identite du morceau courant (artiste + titre). Sert d'unique declencheur
    /// au redecodage de la pochette : tant qu'elle ne change pas, on ne touche
    /// pas au thumbnail, meme si des dizaines d'evenements de lecture arrivent.
    /// </summary>
    private string _trackKey = string.Empty;

    /// <summary>Vrai quand une session media existe et qu'elle joue.</summary>
    public event Action<bool>? PlayingChanged;

    /// <summary>
    /// Nouvelle pochette a afficher. Le parametre vaut null quand le morceau
    /// n'en expose pas : le widget affiche alors son label neutre.
    /// </summary>
    public event Action<byte[]?>? ArtworkChanged;

    /// <summary>
    /// Presence d'une session media exploitable. Faux quand plus aucun lecteur
    /// n'est actif : le widget se masque.
    /// </summary>
    public event Action<bool>? SessionAvailabilityChanged;

    /// <summary>
    /// Le morceau a change : titre puis artiste. Emis juste avant la nouvelle
    /// pochette, pour que les traitements dependant du titre puissent repartir
    /// de zero.
    /// </summary>
    public event Action<string, string>? TrackChanged;

    /// <summary>Le lecteur expose-t-il un morceau suivant ?</summary>
    public bool CanSkipNext
    {
        get
        {
            try { return _session?.GetPlaybackInfo()?.Controls.IsNextEnabled ?? false; }
            catch { return false; }
        }
    }

    /// <summary>Le lecteur expose-t-il un morceau precedent ?</summary>
    public bool CanSkipPrevious
    {
        get
        {
            try { return _session?.GetPlaybackInfo()?.Controls.IsPreviousEnabled ?? false; }
            catch { return false; }
        }
    }

    public async void SkipNext()
    {
        var session = _session;
        if (session is null) return;

        try { await session.TrySkipNextAsync(); }
        catch { /* Le lecteur refuse ou a disparu. */ }
    }

    public async void SkipPrevious()
    {
        var session = _session;
        if (session is null) return;

        try { await session.TrySkipPreviousAsync(); }
        catch { /* Le lecteur refuse ou a disparu. */ }
    }

    /// <summary>
    /// Le lecteur accepte-t-il qu'on lui impose une position ? Spotify et
    /// Chrome le permettent, mais rien ne l'oblige : sans cela, le scratch n'a
    /// pas de sens et le geste retombe sur un simple clic.
    /// </summary>
    public bool CanSeek
    {
        get
        {
            try { return _session?.GetPlaybackInfo()?.Controls.IsPlaybackPositionEnabled ?? false; }
            catch { return false; }
        }
    }

    /// <summary>Position et duree courantes, ou zero si indisponibles.</summary>
    public (TimeSpan Position, TimeSpan Duration) Timeline
    {
        get
        {
            try
            {
                var timeline = _session?.GetTimelineProperties();
                if (timeline is null) return (TimeSpan.Zero, TimeSpan.Zero);
                return (timeline.Position, timeline.EndTime);
            }
            catch
            {
                return (TimeSpan.Zero, TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// Impose une position de lecture. La valeur est bornee par l'appelant ;
    /// un echec du lecteur est sans consequence.
    /// </summary>
    public async void Seek(TimeSpan position)
    {
        var session = _session;
        if (session is null) return;

        try
        {
            await session.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch
        {
            // Le lecteur refuse ou a disparu.
        }
    }

    public MediaSession(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Recupere le gestionnaire SMTC et s'abonne au changement de session
    /// courante. Appele une seule fois au demarrage.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            // SMTC indisponible (edition de Windows sans le composant media) :
            // le widget restera simplement masque.
            _ = _dispatcher.BeginInvoke(() => SessionAvailabilityChanged?.Invoke(false));
            return;
        }

        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        AttachToCurrentSession();
    }

    /// <summary>
    /// Bascule lecture/pause sur la session courante. Sans session, ne fait
    /// rien : le cas se produit reellement quand Apple Music se ferme entre
    /// l'affichage du widget et le clic.
    /// </summary>
    public async void TogglePlayPause()
    {
        var session = _session;
        if (session is null) return;

        try
        {
            await session.TryTogglePlayPauseAsync();
        }
        catch
        {
            // Le lecteur a disparu entre-temps, ou refuse la commande.
        }
    }

    // --- Suivi de la session courante -------------------------------------

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => AttachToCurrentSession();

    /// <summary>
    /// Detache l'ancienne session, accroche la nouvelle. C'est ici que se joue
    /// la robustesse vis-a-vis d'Apple Music, dont la session apparait et
    /// disparait au gre du lancement de l'application.
    /// </summary>
    private void AttachToCurrentSession()
    {
        DetachSession();

        try
        {
            _session = _manager?.GetCurrentSession();
        }
        catch
        {
            _session = null;
        }

        if (_session is null)
        {
            // Plus aucun lecteur : on oublie le morceau courant pour que la
            // pochette soit bien redecodee au retour d'une session.
            _trackKey = string.Empty;
            _dispatcher.BeginInvoke(() =>
            {
                PlayingChanged?.Invoke(false);
                SessionAvailabilityChanged?.Invoke(false);
            });
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;

        _dispatcher.BeginInvoke(() => SessionAvailabilityChanged?.Invoke(true));

        // Etat initial : la session existe deja au moment ou on s'y accroche,
        // aucun evenement ne sera emis pour l'etat courant.
        PublishPlaybackState();
        _ = RefreshMediaPropertiesAsync();
    }

    private void DetachSession()
    {
        if (_session is null) return;

        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch
        {
            // L'objet WinRT peut deja etre mort si le lecteur s'est ferme.
        }

        _session = null;
    }

    // --- Evenements de la session -----------------------------------------

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => PublishPlaybackState();

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => _ = RefreshMediaPropertiesAsync();

    /// <summary>
    /// Lit l'etat de lecture et le republie sur le thread d'interface.
    /// </summary>
    private void PublishPlaybackState()
    {
        bool playing;
        try
        {
            var info = _session?.GetPlaybackInfo();
            playing = info?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            playing = false;
        }

        _dispatcher.BeginInvoke(() => PlayingChanged?.Invoke(playing));
    }

    /// <summary>
    /// Relit les metadonnees. La pochette n'est extraite que si le couple
    /// (artiste, titre) a effectivement change : c'est la garantie qu'un
    /// morceau qu'on met en pause et qu'on relance ne provoque aucun decodage.
    /// </summary>
    private async Task RefreshMediaPropertiesAsync()
    {
        var session = _session;
        if (session is null) return;

        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch
        {
            return;
        }

        if (props is null) return;

        var key = props.Artist + " " + props.Title;
        if (key == _trackKey) return;
        _trackKey = key;

        // Le tempo detecte pour le morceau precedent n'a plus cours.
        var title = props.Title ?? string.Empty;
        var artist = props.Artist ?? string.Empty;
        _ = _dispatcher.BeginInvoke(() => TrackChanged?.Invoke(title, artist));

        var bytes = await ReadThumbnailAsync(props.Thumbnail);

        // La session a pu changer pendant la lecture du flux : on ne publie la
        // pochette que si elle correspond toujours au morceau attendu.
        if (_trackKey != key) return;

        _ = _dispatcher.BeginInvoke(() => ArtworkChanged?.Invoke(bytes));
    }

    /// <summary>
    /// Vide un IRandomAccessStreamReference dans un tableau d'octets.
    /// La lecture passe par DataReader, l'API WinRT native : cela evite de
    /// dependre des extensions de flux WinRT, dont la disponibilite varie
    /// selon la configuration du projet.
    /// </summary>
    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream is null || stream.Size == 0) return null;

            // Garde-fou : une pochette depasse rarement 2 Mo. Au-dela, on
            // considere le flux suspect et on l'ignore plutot que de charger
            // des dizaines de megaoctets en memoire.
            if (stream.Size > 2 * 1024 * 1024) return null;

            var size = (uint)stream.Size;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);

            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            // Pochette illisible : le widget affichera son label neutre.
            return null;
        }
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            try { _manager.CurrentSessionChanged -= OnCurrentSessionChanged; } catch { }
            _manager = null;
        }

        DetachSession();
    }
}
