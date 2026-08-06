using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vinlyo;

/// <summary>
/// Reglages du widget. Tout s'applique en direct : il n'y a ni bouton
/// « Appliquer » ni possibilite d'annuler, parce que chaque reglage se juge a
/// l'oeil sur le disque lui-meme. L'ecriture sur disque a lieu a la fermeture.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Config _config;
    private readonly MainWindow _widget;

    /// <summary>
    /// Vrai pendant le remplissage initial des controles. Sans ce garde-fou,
    /// chaque valeur posee declencherait son propre gestionnaire et
    /// reappliquerait des reglages qui n'ont pas change.
    /// </summary>
    private bool _loading = true;

    public SettingsWindow(Config config, MainWindow widget)
    {
        _config = config;
        _widget = widget;

        InitializeComponent();

        SizeSlider.Value = Math.Clamp(_config.DiscSize, 120, 400);
        TintCheck.IsChecked = _config.Tint;
        TintSlider.Value = Math.Clamp(_config.TintStrength, 0, 1);

        FixedRadio.IsChecked = !_config.BpmSync;
        BpmRadio.IsChecked = _config.BpmSync;
        TurnSlider.Value = Math.Clamp(_config.SecondsPerTurn, 1, 20);

        SelectBeats(_config.BeatsPerTurn);

        InfoCheck.IsChecked = _config.ShowTrackInfo;
        BelowRadio.IsChecked = _config.TrackInfoBelow;
        RightRadio.IsChecked = !_config.TrackInfoBelow;

        ScratchSlider.Value = Math.Clamp(_config.ScratchSecondsPerTurn, 5, 120);

        LockCheck.IsChecked = _config.Locked;
        StartupCheck.IsChecked = StartupShortcut.IsEnabled;

        _loading = false;

        UpdateReadouts();
        UpdateBpm(_widget.CurrentBpm);
        ApplyAccent(_widget.AccentColor);

        _widget.BpmChanged += UpdateBpm;
        _widget.AccentChanged += ApplyAccent;

        Closed += (_, _) =>
        {
            _widget.BpmChanged -= UpdateBpm;
            _widget.AccentChanged -= ApplyAccent;
        };
    }

    private void SelectBeats(int beats)
    {
        Beat1.IsChecked = beats == 1;
        Beat2.IsChecked = beats == 2;
        Beat8.IsChecked = beats == 8;

        // 4 par defaut : c'est une mesure, et c'est ce qui donne la rotation la
        // plus naturelle sur la majorite des morceaux.
        Beat4.IsChecked = beats != 1 && beats != 2 && beats != 8;
    }

    // --- Habillage ----------------------------------------------------------

    /// <summary>
    /// Repeint l'accent de la fenetre avec la couleur du morceau en cours.
    /// C'est le seul point de couleur de l'interface : tout le reste est
    /// volontairement neutre.
    /// </summary>
    private void ApplyAccent(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources["AccentBrush"] = brush;
    }

    private void UpdateReadouts()
    {
        SizeValue.Text = $"{(int)SizeSlider.Value} px";
        TintValue.Text = $"{(int)Math.Round(TintSlider.Value * 100)} %";
        TurnValue.Text = $"{TurnSlider.Value:0.#} s";
        ScratchValue.Text = $"{(int)ScratchSlider.Value} s";

        // Les reglages qui ne servent pas sont estompes plutot que masques :
        // on garde la structure de la fenetre stable, sans laisser croire
        // qu'un curseur inerte agit.
        var tint = TintCheck.IsChecked == true;
        TintSlider.IsEnabled = tint;
        TintLabel.Opacity = tint ? 1 : 0.35;

        var bpmMode = BpmRadio.IsChecked == true;
        TurnSlider.IsEnabled = !bpmMode;
        TurnLabel.Opacity = bpmMode ? 0.35 : 1;

        foreach (var segment in new[] { Beat1, Beat2, Beat4, Beat8 }) segment.IsEnabled = bpmMode;
        BeatsLabel.Opacity = bpmMode ? 1 : 0.35;
        BeatsUnit.Opacity = bpmMode ? 1 : 0.35;
        TempoLabel.Opacity = bpmMode ? 1 : 0.35;
        BpmText.Opacity = bpmMode ? 1 : 0.35;
        DoubleButton.IsEnabled = bpmMode;
        HalveButton.IsEnabled = bpmMode;

        var info = InfoCheck.IsChecked == true;
        BelowRadio.IsEnabled = info;
        RightRadio.IsEnabled = info;
        PlacementLabel.Opacity = info ? 1 : 0.35;
    }

    private void UpdateBpm(double bpm)
    {
        // Tirets plutot qu'une phrase : la valeur est en chasse fixe, comme un
        // afficheur d'appareil, et « pas encore de mesure » s'y lit mieux ainsi
        // qu'avec du texte courant qui aurait l'air deforme.
        BpmText.Text = bpm > 0 ? $"{bpm:0} BPM" : "--- BPM";
    }

    // --- Barre de titre -----------------------------------------------------

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        // La fenetre n'a pas de chrome systeme : c'est cette bande qui la
        // deplace. DragMove leve si le bouton est relache entre-temps.
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    // --- Disque -------------------------------------------------------------

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _config.DiscSize = (int)SizeSlider.Value;
        SizeValue.Text = $"{_config.DiscSize} px";
        _widget.RefreshAppearance();
    }

    private void OnTintToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.Tint = TintCheck.IsChecked == true;
        UpdateReadouts();
        _widget.RefreshAppearance();
    }

    private void OnTintStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _config.TintStrength = TintSlider.Value;
        TintValue.Text = $"{(int)Math.Round(_config.TintStrength * 100)} %";
        _widget.RefreshAppearance();
    }

    // --- Rotation -----------------------------------------------------------

    private void OnSpeedModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.BpmSync = BpmRadio.IsChecked == true;
        UpdateReadouts();
        _widget.RefreshSpeed();
    }

    private void OnTurnSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _config.SecondsPerTurn = TurnSlider.Value;
        TurnValue.Text = $"{_config.SecondsPerTurn:0.#} s";
        _widget.RefreshSpeed();
    }

    private void OnBeatsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.BeatsPerTurn =
            Beat1.IsChecked == true ? 1 :
            Beat2.IsChecked == true ? 2 :
            Beat8.IsChecked == true ? 8 : 4;

        _widget.RefreshSpeed();
    }

    private void OnDoubleBpm(object sender, RoutedEventArgs e) => _widget.ScaleBpm(2);

    private void OnHalveBpm(object sender, RoutedEventArgs e) => _widget.ScaleBpm(0.5);

    // --- Affichage ----------------------------------------------------------

    private void OnInfoToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.ShowTrackInfo = InfoCheck.IsChecked == true;
        UpdateReadouts();
        _widget.RefreshAppearance();
    }

    private void OnPlacementChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.TrackInfoBelow = BelowRadio.IsChecked == true;
        _widget.RefreshAppearance();
    }

    // --- Gestes -------------------------------------------------------------

    private void OnScratchChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _config.ScratchSecondsPerTurn = ScratchSlider.Value;
        ScratchValue.Text = $"{(int)_config.ScratchSecondsPerTurn} s";
    }

    // --- Systeme ------------------------------------------------------------

    private void OnLockToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.Locked = LockCheck.IsChecked == true;
        _widget.RefreshLockState();
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        StartupShortcut.Set(StartupCheck.IsChecked == true);

        // On relit l'etat reel du disque : si la creation du raccourci a
        // echoue, la case ne doit pas rester cochee a tort.
        _loading = true;
        StartupCheck.IsChecked = StartupShortcut.IsEnabled;
        _loading = false;
    }

    /// <summary>Synchronise la case avec le menu contextuel du widget.</summary>
    public void RefreshLockState()
    {
        _loading = true;
        LockCheck.IsChecked = _config.Locked;
        _loading = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
