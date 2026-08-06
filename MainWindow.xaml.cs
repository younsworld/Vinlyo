using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Vinlyo;

public partial class MainWindow : Window
{
    /// <summary>Dimensions du trace de reference, avant mise a l'echelle.</summary>
    private const double DesignSize = 252;
    private const double DesignDiscSize = 220;
    private const double DesignLabelSize = 96;

    /// <summary>
    /// Encombrement du cartouche titre et artiste, en unites de trace.
    ///
    /// Sous le disque il occupe presque toute la largeur de la fenetre, qui est
    /// deja fixee par le disque : la restreindre n'economiserait rien et
    /// tronquerait des titres pour rien. A droite, en revanche, chaque pixel
    /// elargit la fenetre, d'ou une mesure plus courte.
    /// </summary>
    private const double TrackInfoWidthBelow = 236;
    private const double TrackInfoWidthBeside = 200;
    private const double TrackInfoHeight = 58;

    /// <summary>
    /// Rayon de la zone « pochette », en unites de trace. Legerement au-dela du
    /// label lui-meme pour englober son lisere : viser exactement 48 rendrait la
    /// bordure inerte, ce qui se remarque a l'usage.
    /// </summary>
    private const double LabelRadius = 51;

    /// <summary>
    /// En deca de cet angle, le geste est considere comme un clic et non comme
    /// un scratch. Sans ce seuil, le moindre tremblement de la main
    /// transformerait un play/pause en saut dans le morceau.
    /// </summary>
    private const double ScratchDeadZoneDegrees = 6;

    /// <summary>
    /// Trop pres du centre, l'angle du pointeur devient chaotique : quelques
    /// pixels suffisent alors a le faire varier de dizaines de degres.
    /// </summary>
    private const double ScratchMinRadius = 18;

    private readonly Config _config = Config.Load();
    private readonly MediaSession _media;
    private readonly TempoTracker _tempo = new();
    private readonly AudioLoopback _audio = new();

    private Storyboard? _spin;
    private bool _playing;

    /// <summary>Pinceau du label quand aucune pochette n'est disponible.</summary>
    private readonly Brush _blankLabel;

    /// <summary>Octets de la pochette courante, gardes pour pouvoir redecoder
    /// a la bonne definition si la taille du disque change.</summary>
    private byte[]? _artwork;

    /// <summary>Teinte dominante de la pochette courante, si elle en a une.</summary>
    private (double Hue, double Saturation)? _dominant;

    private double _bpm;
    private SettingsWindow? _settings;

    /// <summary>
    /// Compteur de clics sur la pochette et minuterie qui clot la sequence.
    ///
    /// Distinguer un clic de deux ou trois impose d'attendre : tant que la
    /// fenetre de double-clic du systeme n'est pas ecoulee, on ignore si un
    /// autre clic arrive. Cette minuterie ne scrute rien, elle ne s'arme que
    /// pendant cette attente et se desarme aussitot apres.
    ///
    /// C'est le prix du geste demande, et la raison pour laquelle un clic sur
    /// le disque lui-meme declenche la pause sans le moindre delai.
    /// </summary>
    private readonly DispatcherTimer _clickSequence = new();

    private int _pendingClicks;

    /// <summary>Couleur d'accent tiree de la pochette courante.</summary>
    public Color AccentColor { get; private set; } = Color.FromRgb(0xB9, 0xAF, 0x9E);

    /// <summary>Emis quand l'accent change, pour que la fenetre de parametres
    /// suive la couleur du morceau en cours.</summary>
    public event Action<Color>? AccentChanged;

    // Etat du geste en cours.
    private enum Gesture { None, Move, Scratch }

    private Gesture _gesture;
    private bool _pressed;
    private Point _grabOffset;

    private bool _moved;
    private bool _pressedOnLabel;
    private double _lastPointerAngle;
    private double _scratchTravel;
    private double _discAngleAtGrab;
    private TimeSpan _positionAtGrab;
    private TimeSpan _durationAtGrab;

    /// <summary>Tempo courant en battements par minute, ou 0 si inconnu.</summary>
    public double CurrentBpm => _bpm;

    /// <summary>Emis quand l'estimation de tempo change.</summary>
    public event Action<double>? BpmChanged;

    public MainWindow()
    {
        InitializeComponent();

        _blankLabel = Label.Fill;
        _media = new MediaSession(Dispatcher);

        // La fenetre de double-clic du systeme est le bon reglage : c'est celui
        // que l'utilisateur a deja choisi pour tout le reste de Windows. On la
        // plafonne quand meme, au-dela l'attente avant la pause se sent trop.
        _clickSequence.Interval = TimeSpan.FromMilliseconds(Math.Min(GetDoubleClickTime(), 450));
        _clickSequence.Tick += OnClickSequenceEnded;

        ApplyLayout();
        RestorePosition();

        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _clickSequence.Stop();
            SavePosition();
            _audio.Dispose();
            _media.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LockItem.IsChecked = _config.Locked;
        StartupItem.IsChecked = StartupShortcut.IsEnabled;

        ApplyTint();
        ApplySpeed();

        _media.PlayingChanged += OnPlayingChanged;
        _media.ArtworkChanged += OnArtworkChanged;
        _media.SessionAvailabilityChanged += OnSessionAvailabilityChanged;
        _media.TrackChanged += OnTrackChanged;

        // La detection tourne sur le thread de capture audio : le retour vers
        // l'interface doit repasser explicitement par le Dispatcher.
        _tempo.BpmChanged += bpm => Dispatcher.BeginInvoke(() => OnBpmChanged(bpm));
        _audio.SamplesAvailable += (buffer, count, rate) => _tempo.Push(buffer, count, rate);

        await _media.StartAsync();
    }

    // --- Taille et position -------------------------------------------------

    /// <summary>
    /// Applique la taille choisie et la disposition du cartouche de texte.
    ///
    /// Le trace reste defini a 220 pixels : seule la mise a l'echelle change,
    /// ce qui evite de recalculer huit rayons de sillons et garde le rendu net
    /// a toutes les tailles. La fenetre est dimensionnee a la main plutot que
    /// par SizeToContent, parce que la position enregistree se calcule avant
    /// que WPF ait mesure quoi que ce soit.
    /// </summary>
    private void ApplyLayout()
    {
        var diameter = Math.Clamp(_config.DiscSize, 120, 400);
        var scale = diameter / DesignDiscSize;

        Scale.ScaleX = scale;
        Scale.ScaleY = scale;

        var showInfo = _config.ShowTrackInfo;
        var below = _config.TrackInfoBelow;

        TrackInfo.Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
        TrackInfo.Width = below ? TrackInfoWidthBelow : TrackInfoWidthBeside;
        Stack.Orientation = below ? Orientation.Vertical : Orientation.Horizontal;

        if (below)
        {
            TrackInfo.HorizontalAlignment = HorizontalAlignment.Center;
            TrackInfo.VerticalAlignment = VerticalAlignment.Top;
            TrackInfo.Margin = new Thickness(0, 2, 0, 0);
            TrackTitle.TextAlignment = TextAlignment.Center;
            TrackArtist.TextAlignment = TextAlignment.Center;
            TrackRule.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            TrackInfo.HorizontalAlignment = HorizontalAlignment.Left;
            TrackInfo.VerticalAlignment = VerticalAlignment.Center;
            TrackInfo.Margin = new Thickness(4, 0, 0, 0);
            TrackTitle.TextAlignment = TextAlignment.Left;
            TrackArtist.TextAlignment = TextAlignment.Left;
            TrackRule.HorizontalAlignment = HorizontalAlignment.Left;
        }

        var contentWidth = DesignSize + (showInfo && !below ? TrackInfoWidthBeside + 4 : 0);
        var contentHeight = DesignSize + (showInfo && below ? TrackInfoHeight : 0);

        Width = contentWidth * scale;
        Height = contentHeight * scale;
    }

    /// <summary>
    /// Centre du disque dans les coordonnees de la fenetre. Il ne coincide plus
    /// avec le centre de la fenetre des que le cartouche de texte est affiche :
    /// tout le calcul d'angle du scratch en depend.
    /// </summary>
    private Point DiscCenter() =>
        DiscBox.TranslatePoint(new Point(DesignSize / 2, DesignSize / 2), this);

    /// <summary>Distance du pointeur au centre du disque, ramenee aux unites
    /// du trace pour pouvoir la comparer aux rayons de reference.</summary>
    private double RadiusFromCenter(Point position)
    {
        var center = DiscCenter();
        var dx = position.X - center.X;
        var dy = position.Y - center.Y;
        var scale = Scale.ScaleX <= 0 ? 1 : Scale.ScaleX;

        return Math.Sqrt(dx * dx + dy * dy) / scale;
    }

    /// <summary>
    /// Replace la fenetre la ou elle etait, en verifiant qu'elle tombe toujours
    /// sur un ecran existant : un moniteur debranche depuis la derniere session
    /// enverrait sinon le widget dans le vide.
    /// </summary>
    private void RestorePosition()
    {
        if (_config.Left is double left && _config.Top is double top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
            return;
        }

        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 40;
        Top = work.Bottom - Height - 40;
    }

    private bool IsOnScreen(double left, double top)
    {
        // On exige simplement que le centre du widget tombe dans le bureau
        // virtuel, ce qui tolere un debordement partiel choisi par l'utilisateur.
        var cx = left + Width / 2;
        var cy = top + Height / 2;

        return cx >= SystemParameters.VirtualScreenLeft
            && cy >= SystemParameters.VirtualScreenTop
            && cx <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && cy <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }

    private void SavePosition()
    {
        _config.Left = Left;
        _config.Top = Top;
        _config.Save();
    }

    // --- Rotation -----------------------------------------------------------

    /// <summary>
    /// Duree d'un tour. En mode BPM elle suit le tempo detecte ; a defaut de
    /// tempo fiable, on retombe sur la vitesse fixe plutot que sur une valeur
    /// arbitraire.
    /// </summary>
    private double TurnSeconds
    {
        get
        {
            if (_config.BpmSync && _bpm > 0)
                return Math.Clamp(60.0 / _bpm * Math.Max(1, _config.BeatsPerTurn), 0.4, 60);

            return Math.Clamp(_config.SecondsPerTurn, 0.4, 60);
        }
    }

    /// <summary>
    /// (Re)construit l'animation de rotation en repartant de l'angle courant.
    /// Repartir de zero ferait sauter le disque a chaque changement de tempo.
    /// </summary>
    private void ApplySpeed()
    {
        var angle = Rotation.Angle;

        if (_spin is not null)
        {
            _spin.Stop(this);
            _spin.Remove(this);
            _spin = null;
        }

        var animation = new DoubleAnimation
        {
            From = angle,
            To = angle + 360,
            Duration = new Duration(TimeSpan.FromSeconds(TurnSeconds)),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, Label);
        Storyboard.SetTargetProperty(animation,
            new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        _spin = new Storyboard();
        _spin.Children.Add(animation);

        // La transparence par pixel de la fenetre force WPF en rendu logiciel :
        // chaque image est composee par le processeur. Les 60 images par seconde
        // par defaut n'apportent rien a un disque qui met plusieurs secondes a
        // faire un tour, et coutent exactement le double de 30.
        //
        // La cadence doit imperativement etre posee sur la timeline racine,
        // c'est-a-dire le Storyboard : sur une animation enfant, WPF l'ignore
        // en silence.
        Timeline.SetDesiredFrameRate(_spin, 30);

        // isControllable a true est indispensable : sans lui, l'horloge de
        // l'animation n'est pas conservee et Pause()/Resume() n'ont aucun effet.
        _spin.Begin(this, true);
        if (!_playing) _spin.Pause(this);
    }

    /// <summary>
    /// Detache l'animation et fige l'angle courant comme valeur locale, pour
    /// que le scratch puisse ensuite le piloter directement.
    /// </summary>
    private void ReleaseSpin()
    {
        if (_spin is null) return;

        var angle = Rotation.Angle;
        _spin.Stop(this);
        _spin.Remove(this);
        _spin = null;

        Rotation.Angle = angle;
    }

    private void OnPlayingChanged(bool playing)
    {
        _playing = playing;

        if (_spin is not null && _gesture != Gesture.Scratch)
        {
            // Pause() fige l'horloge : le disque reste exactement a l'angle
            // courant. Stop() ramenerait l'angle a zero, ce qui donnerait un
            // a-coup visible a chaque mise en pause.
            if (playing) _spin.Resume(this);
            else _spin.Pause(this);
        }

        UpdateCapture();
    }

    // --- Tempo --------------------------------------------------------------

    /// <summary>
    /// La capture audio ne tourne que si elle sert a quelque chose : mode BPM
    /// actif et lecture en cours. Le reste du temps, le thread est arrete.
    /// </summary>
    private void UpdateCapture()
    {
        var wanted = _config.BpmSync && _playing;

        if (wanted && !_audio.IsRunning) _audio.Start();
        else if (!wanted && _audio.IsRunning) { _audio.Stop(); _tempo.Reset(); }
    }

    private void OnBpmChanged(double bpm)
    {
        _bpm = bpm;
        BpmChanged?.Invoke(bpm);

        if (_config.BpmSync) ApplySpeed();
    }

    private void OnTrackChanged(string title, string artist)
    {
        // Le tempo du morceau precedent n'a plus cours : mieux vaut ne rien
        // afficher qu'une valeur heritee.
        _tempo.Reset();

        TrackTitle.Text = string.IsNullOrWhiteSpace(title) ? "—" : title;
        TrackArtist.Text = artist ?? string.Empty;

        // Le filet ne se justifie que s'il separe deux choses.
        TrackRule.Visibility = string.IsNullOrWhiteSpace(artist)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>Double ou divise par deux le tempo retenu. L'estimation
    /// automatique se trompe regulierement d'un facteur deux.</summary>
    public void ScaleBpm(double factor)
    {
        if (_bpm <= 0) return;

        _bpm = Math.Clamp(_bpm * factor, 30, 300);
        BpmChanged?.Invoke(_bpm);

        if (_config.BpmSync) ApplySpeed();
    }

    // --- Pochette et teinte -------------------------------------------------

    private void OnArtworkChanged(byte[]? data)
    {
        _artwork = data;
        _dominant = data is null ? null : Palette.Dominant(data);

        ApplyArtwork();
        ApplyTint();
    }

    /// <summary>
    /// Decode la pochette a la definition reellement affichee. N'est appele
    /// que sur un changement de morceau ou de taille : MediaSession filtre les
    /// evenements de lecture en amont.
    /// </summary>
    private void ApplyArtwork()
    {
        if (_artwork is null || _artwork.Length == 0)
        {
            Label.Fill = _blankLabel;
            return;
        }

        try
        {
            // Le decodeur ne produit que la taille reellement affichee, mise a
            // l'echelle du moniteur : une pochette de 1400 pixels n'occupera
            // jamais plus que les quelques centaines utiles.
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var displayed = DesignLabelSize * Scale.ScaleX * dpi;
            var target = Math.Max(1, (int)Math.Ceiling(displayed));

            using var stream = new MemoryStream(_artwork);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // libere le flux des la fin du decodage
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = target;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            brush.Freeze();

            Label.Fill = brush;
        }
        catch
        {
            // Format d'image non gere : on retombe sur le label neutre.
            Label.Fill = _blankLabel;
        }
    }

    /// <summary>
    /// Colore le corps du disque et les sillons a partir de la teinte dominante
    /// de la pochette. La valeur reste volontairement tres basse : l'objectif
    /// est un vinyle teinte, pas une assiette de couleur.
    /// </summary>
    private void ApplyTint()
    {
        Color inner, middle, outer, groove;

        if (_config.Tint && _dominant is { } dominant)
        {
            var strength = Math.Clamp(_config.TintStrength, 0, 1);
            var saturation = Math.Min(1, dominant.Saturation) * strength;
            var hue = dominant.Hue;

            inner = Palette.FromHsv(hue, saturation * 0.85, 0.20);
            middle = Palette.FromHsv(hue, saturation * 0.95, 0.09);
            outer = Palette.FromHsv(hue, saturation * 0.80, 0.035);
            groove = Palette.FromHsv(hue, saturation * 0.60, 0.17);
        }
        else
        {
            inner = Color.FromRgb(0x22, 0x22, 0x22);
            middle = Color.FromRgb(0x0E, 0x0E, 0x0E);
            outer = Color.FromRgb(0x05, 0x05, 0x05);
            groove = Color.FromRgb(0x1E, 0x1E, 0x1E);
        }

        var disc = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.65,
            RadiusY = 0.65,
        };
        disc.GradientStops.Add(new GradientStop(inner, 0));
        disc.GradientStops.Add(new GradientStop(middle, 0.6));
        disc.GradientStops.Add(new GradientStop(outer, 1));
        disc.Freeze();

        var grooveBrush = new SolidColorBrush(groove);
        grooveBrush.Freeze();

        // L'accent partage la teinte du disque mais pas sa valeur : a 0,20 il
        // serait illisible en texte ou en filet. On le remonte a 0,72 avec une
        // saturation moderee, ce qui donne une couleur qui tient sur le noir
        // sans virer au fluo.
        AccentColor = _config.Tint && _dominant is { } accentSource
            ? Palette.FromHsv(accentSource.Hue, 0.45, 0.72)
            : Color.FromRgb(0xB9, 0xAF, 0x9E);

        var accentBrush = new SolidColorBrush(AccentColor);
        accentBrush.Freeze();

        // Les references dans le XAML sont dynamiques : remplacer la ressource
        // suffit a repeindre le disque.
        Resources["DiscBrush"] = disc;
        Resources["GrooveBrush"] = grooveBrush;
        Resources["AccentBrush"] = accentBrush;

        AccentChanged?.Invoke(AccentColor);
    }

    private void OnSessionAvailabilityChanged(bool available)
    {
        Visibility = available ? Visibility.Visible : Visibility.Hidden;

        // Reafficher une fenetre la remonte dans l'ordre d'empilement : on la
        // renvoie aussitot au fond.
        if (available) SendToBottom();
    }

    // --- Souris : clic, scratch et deplacement -------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _pressed = true;
        _moved = false;
        _grabOffset = e.GetPosition(this);

        // La pochette et la surface du disque ne repondent pas au meme geste :
        // on retient des maintenant ou l'appui a commence.
        _pressedOnLabel = RadiusFromCenter(_grabOffset) <= LabelRadius;

        var wantsMove = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (wantsMove)
        {
            _gesture = _config.Locked ? Gesture.None : Gesture.Move;
        }
        else if (_media.CanSeek)
        {
            _gesture = Gesture.Scratch;

            var (position, duration) = _media.Timeline;
            _positionAtGrab = position;
            _durationAtGrab = duration;

            _scratchTravel = 0;
            _lastPointerAngle = PointerAngle(_grabOffset);

            // Le disque doit suivre le doigt : on detache l'animation pour
            // pouvoir imposer l'angle directement.
            ReleaseSpin();
            _discAngleAtGrab = Rotation.Angle;
        }
        else
        {
            // Lecteur qui refuse le changement de position : le geste ne peut
            // etre qu'un clic.
            _gesture = Gesture.None;
        }

        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_pressed) return;

        var position = e.GetPosition(this);

        switch (_gesture)
        {
            case Gesture.Move:
                MoveWindowTo(position);
                break;

            case Gesture.Scratch:
                Scratch(position);
                break;
        }
    }

    private void MoveWindowTo(Point position)
    {
        if (!_moved)
        {
            // En deca du seuil systeme, le geste reste un clic.
            if (Math.Abs(position.X - _grabOffset.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _grabOffset.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _moved = true;
        }

        // PointToScreen rend des pixels physiques, alors que Left/Top se
        // comptent en unites independantes du peripherique : la conversion est
        // obligatoire des que l'affichage n'est pas a 100 %.
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;

        var cursor = source.CompositionTarget.TransformFromDevice.Transform(PointToScreen(position));

        Left = cursor.X - _grabOffset.X;
        Top = cursor.Y - _grabOffset.Y;
    }

    /// <summary>
    /// Fait suivre le disque au pointeur et cumule l'angle parcouru. Le cumul
    /// est signe et non borne : deux tours en arriere reculent deux fois plus
    /// qu'un seul.
    /// </summary>
    private void Scratch(Point position)
    {
        // Trop pres du centre, l'angle n'a plus de sens : on ignore le
        // mouvement plutot que de faire tressauter le disque.
        if (RadiusFromCenter(position) * Scale.ScaleX < ScratchMinRadius) return;

        var angle = PointerAngle(position);
        var delta = angle - _lastPointerAngle;

        // Le passage par le haut du disque fait sauter l'angle de 360 degres :
        // on ramene toujours l'ecart dans le demi-tour le plus court.
        while (delta > 180) delta -= 360;
        while (delta < -180) delta += 360;

        _lastPointerAngle = angle;
        _scratchTravel += delta;
        _moved = true;

        Rotation.Angle = _discAngleAtGrab + _scratchTravel;
    }

    private double PointerAngle(Point position)
    {
        var center = DiscCenter();
        return Math.Atan2(position.Y - center.Y, position.X - center.X) * 180.0 / Math.PI;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_pressed) return;

        _pressed = false;
        ReleaseMouseCapture();

        var gesture = _gesture;
        _gesture = Gesture.None;

        switch (gesture)
        {
            case Gesture.Move when _moved:
                SavePosition();
                return;

            case Gesture.Scratch when Math.Abs(_scratchTravel) >= ScratchDeadZoneDegrees:
                CommitScratch();
                return;

            case Gesture.Scratch:
                // Sous le seuil : c'etait un clic, pas un scratch.
                ApplySpeed();
                break;
        }

        RegisterClick();
    }

    /// <summary>
    /// Aiguille le clic selon l'endroit ou il a commence. Sur la surface du
    /// disque, la pause part immediatement. Sur la pochette, il faut attendre
    /// de savoir si d'autres clics suivent.
    /// </summary>
    private void RegisterClick()
    {
        if (!_pressedOnLabel)
        {
            _media.TogglePlayPause();
            return;
        }

        // Trois clics suffisent : au-dela, on cesse de compter plutot que de
        // laisser une rafale accidentelle remonter tout l'album.
        _pendingClicks = Math.Min(_pendingClicks + 1, 3);

        _clickSequence.Stop();
        _clickSequence.Start();
    }

    private void OnClickSequenceEnded(object? sender, EventArgs e)
    {
        _clickSequence.Stop();

        var clicks = _pendingClicks;
        _pendingClicks = 0;

        switch (clicks)
        {
            case 1:
                _media.TogglePlayPause();
                break;

            case 2 when _media.CanSkipNext:
                _media.SkipNext();
                break;

            case 3 when _media.CanSkipPrevious:
                _media.SkipPrevious();
                break;
        }
    }

    private void CommitScratch()
    {
        var offset = TimeSpan.FromSeconds(_scratchTravel / 360.0 * _config.ScratchSecondsPerTurn);
        var target = _positionAtGrab + offset;

        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_durationAtGrab > TimeSpan.Zero && target > _durationAtGrab) target = _durationAtGrab;

        _media.Seek(target);

        // La rotation repart de l'angle ou le doigt a lache le disque.
        ApplySpeed();
    }

    // --- Menu contextuel et parametres ---------------------------------------

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        // Pas d'Owner : rattacher cette fenetre au widget lui ferait heriter de
        // sa position au fond de l'ordre d'empilement, donc la rendrait
        // invisible derriere toutes les autres.
        _settings = new SettingsWindow(_config, this);
        _settings.Closed += (_, _) =>
        {
            _settings = null;
            _config.Save();
        };
        _settings.Show();
    }

    /// <summary>Reapplique taille, pochette et teinte. Appele par la fenetre
    /// de parametres a chaque reglage visuel.</summary>
    public void RefreshAppearance()
    {
        ApplyLayout();
        ApplyArtwork();
        ApplyTint();
    }

    /// <summary>Reapplique la vitesse de rotation et l'etat de la capture
    /// audio. Appele par la fenetre de parametres.</summary>
    public void RefreshSpeed()
    {
        ApplySpeed();
        UpdateCapture();
    }

    /// <summary>Synchronise la case du menu contextuel avec le reglage change
    /// depuis la fenetre de parametres.</summary>
    public void RefreshLockState() => LockItem.IsChecked = _config.Locked;

    private void OnToggleLock(object sender, RoutedEventArgs e)
    {
        _config.Locked = ((MenuItem)sender).IsChecked;
        _config.Save();
        _settings?.RefreshLockState();
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e)
    {
        StartupShortcut.Set(((MenuItem)sender).IsChecked);

        // On relit l'etat reel du disque : si la creation du raccourci a echoue,
        // la case ne doit pas rester cochee a tort.
        StartupItem.IsChecked = StartupShortcut.IsEnabled;
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        _settings?.Close();
        Close();
    }

    // --- Interop : maintien au fond de l'ordre d'empilement -------------------

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_MOUSEACTIVATE = 0x0021;

    private const int MA_NOACTIVATE = 3;

    private static readonly IntPtr HWND_BOTTOM = new(1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    // Les variantes ...Ptr sont correctes ici parce que le projet ne cible que
    // win-x64 (voir RuntimeIdentifier dans le .csproj).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>Delai de double-clic configure par l'utilisateur, en ms.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW retire la fenetre de la barre des taches et de
        // l'Alt-Tab. WS_EX_NOACTIVATE l'empeche de prendre le focus.
        var exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE,
            new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        HwndSource.FromHwnd(handle)?.AddHook(WndProc);

        SendToBottom();
    }

    private void SendToBottom()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                // Le seul moyen de tenir la position au fond dans la duree :
                // reecrire chaque demande de repositionnement avant qu'elle ne
                // soit appliquee. Un SetWindowPos ponctuel serait defait des
                // qu'une autre fenetre est activee.
                var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                position.hwndInsertAfter = HWND_BOTTOM;
                position.flags &= ~SWP_NOZORDER;   // sans quoi hwndInsertAfter est ignore
                Marshal.StructureToPtr(position, lParam, false);
                break;

            case WM_MOUSEACTIVATE:
                // WS_EX_NOACTIVATE ne suffit pas : un clic declenche malgre tout
                // une tentative d'activation, qui ramenerait le widget devant
                // les autres fenetres. On la refuse tout en laissant passer le
                // clic lui-meme.
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }
}
