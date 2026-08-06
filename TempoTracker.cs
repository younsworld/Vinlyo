namespace Vinlyo;

/// <summary>
/// Estimation du tempo a partir d'un flux audio mono.
///
/// Le principe tient en trois etages :
///   1. un passe-bas isole le bas du spectre, ou vivent grosse caisse et
///      basse, c'est-a-dire ce qui porte le tempo ;
///   2. l'energie de chaque bloc court donne une enveloppe, dont on ne garde
///      que les hausses : c'est la fonction d'attaque, un signal a 86 Hz qui
///      pique a chaque frappe ;
///   3. l'autocorrelation de cette fonction sur six secondes fait ressortir la
///      periodicite dominante, qu'on convertit en battements par minute.
///
/// Aucune transformee de Fourier : a cette resolution elle n'apporterait rien
/// et couterait bien plus cher.
/// </summary>
public sealed class TempoTracker
{
    /// <summary>Frequence de la fonction d'attaque, en hertz.</summary>
    private const int OnsetRate = 86;

    /// <summary>Six secondes d'historique : assez pour deux mesures lentes.</summary>
    private const int WindowSize = 512;

    private const double MinBpm = 60;
    private const double MaxBpm = 180;

    /// <summary>Tempo de reference du peignage : les estimations qui s'en
    /// eloignent sont penalisees, ce qui limite les erreurs d'octave.</summary>
    private const double PreferredBpm = 125;

    /// <summary>
    /// Largeur du peignage, en logarithme neperien du rapport des tempos.
    ///
    /// Ce reglage arbitre les erreurs d'octave, ou l'on confond un tempo avec
    /// son double ou sa moitie. Trop etroit, tout est ramene vers 125 et les
    /// morceaux rapides sont detectes a la moitie de leur tempo ; trop large,
    /// le peignage ne departage plus rien. La valeur retenue place la limite
    /// haute utilisable vers 180 BPM.
    /// </summary>
    private const double PreferenceWidth = 0.9;

    private readonly float[] _onset = new float[WindowSize];
    private readonly float[] _ordered = new float[WindowSize];
    private readonly List<double> _history = new();

    private int _onsetIndex;
    private int _onsetFilled;
    private int _framesSinceAnalysis;

    private int _sampleRate;
    private int _blockSize;
    private int _blockCount;
    private double _blockEnergy;

    private double _lowpass;
    private double _previousEnergy;

    private double _reported;

    /// <summary>
    /// Tempo estime, ou 0 tant qu'aucune estimation fiable n'est disponible.
    /// Emis depuis le thread de capture : l'abonne doit reexpedier lui-meme
    /// vers son thread d'interface.
    /// </summary>
    public event Action<double>? BpmChanged;

    /// <summary>Oublie l'historique. A appeler au changement de morceau.</summary>
    public void Reset()
    {
        Array.Clear(_onset);
        _onsetIndex = 0;
        _onsetFilled = 0;
        _framesSinceAnalysis = 0;
        _blockCount = 0;
        _blockEnergy = 0;
        _lowpass = 0;
        _previousEnergy = 0;
        _history.Clear();

        if (_reported != 0)
        {
            _reported = 0;
            BpmChanged?.Invoke(0);
        }
    }

    /// <summary>Consomme un paquet d'echantillons mono.</summary>
    public void Push(float[] samples, int count, int sampleRate)
    {
        if (count <= 0 || sampleRate <= 0) return;

        if (sampleRate != _sampleRate)
        {
            _sampleRate = sampleRate;
            _blockSize = Math.Max(1, sampleRate / OnsetRate);
            Reset();
        }

        // Passe-bas a un pole, coupure aux environs de 200 Hz. La constante
        // depend de la frequence d'echantillonnage pour que la coupure reste
        // la meme quel que soit le peripherique.
        var alpha = 1.0 - Math.Exp(-2.0 * Math.PI * 200.0 / sampleRate);

        for (var i = 0; i < count; i++)
        {
            _lowpass += alpha * (samples[i] - _lowpass);
            _blockEnergy += _lowpass * _lowpass;

            if (++_blockCount < _blockSize) continue;

            var energy = _blockEnergy / _blockSize;
            _blockEnergy = 0;
            _blockCount = 0;

            // Seules les hausses d'energie signalent une frappe. Les baisses
            // sont mises a zero : garder la decroissance noierait les pics.
            var flux = energy - _previousEnergy;
            _previousEnergy = energy;

            PushOnset((float)Math.Max(0, flux));
        }
    }

    private void PushOnset(float value)
    {
        _onset[_onsetIndex] = value;
        _onsetIndex = (_onsetIndex + 1) % WindowSize;
        if (_onsetFilled < WindowSize) _onsetFilled++;

        // Une analyse par seconde : le tempo ne change pas plus vite, et
        // l'autocorrelation est de loin l'etape la plus couteuse.
        if (++_framesSinceAnalysis < OnsetRate) return;
        _framesSinceAnalysis = 0;

        if (_onsetFilled < WindowSize) return;

        Analyse();
    }

    private void Analyse()
    {
        // Remise a plat du tampon circulaire dans l'ordre chronologique.
        for (var i = 0; i < WindowSize; i++)
            _ordered[i] = _onset[(_onsetIndex + i) % WindowSize];

        // Centrage : sans lui, la composante continue ecraserait toutes les
        // correlations et le pic serait toujours au decalage le plus court.
        double mean = 0;
        for (var i = 0; i < WindowSize; i++) mean += _ordered[i];
        mean /= WindowSize;

        double variance = 0;
        for (var i = 0; i < WindowSize; i++)
        {
            _ordered[i] = (float)(_ordered[i] - mean);
            variance += _ordered[i] * _ordered[i];
        }

        // Silence, ou signal sans relief : rien a estimer.
        if (variance < 1e-9) { Report(0); return; }

        var minLag = (int)Math.Floor(OnsetRate * 60.0 / MaxBpm);
        var maxLag = (int)Math.Ceiling(OnsetRate * 60.0 / MinBpm);

        var bestLag = -1;
        double bestScore = 0;
        double scoreSum = 0;
        var scoreCount = 0;

        var scores = new double[maxLag + 1];

        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var n = lag; n < WindowSize; n++) sum += _ordered[n] * _ordered[n - lag];

            // Normalisation par le nombre de termes : sans elle, les grands
            // decalages seraient systematiquement desavantages.
            var correlation = sum / (WindowSize - lag);

            var bpm = 60.0 * OnsetRate / lag;
            var deviation = Math.Log(bpm / PreferredBpm) / PreferenceWidth;
            var weighted = correlation * Math.Exp(-0.5 * deviation * deviation);

            scores[lag] = weighted;
            scoreSum += Math.Abs(weighted);
            scoreCount++;

            if (weighted > bestScore) { bestScore = weighted; bestLag = lag; }
        }

        if (bestLag < 0 || scoreCount == 0) { Report(0); return; }

        // Un pic qui ne se detache pas de la moyenne n'est pas un tempo.
        var average = scoreSum / scoreCount;
        if (average <= 0 || bestScore < average * 1.6) { Report(0); return; }

        // Interpolation parabolique : la resolution en decalage entier est
        // grossiere dans le haut de la plage (un cran vaut plus de 10 BPM
        // vers 180), un sommet interpole la rend utilisable.
        var refined = (double)bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
            var denominator = a - 2 * b + c;
            if (Math.Abs(denominator) > 1e-12)
                refined = bestLag + 0.5 * (a - c) / denominator;
        }

        Report(60.0 * OnsetRate / refined);
    }

    private void Report(double bpm)
    {
        if (bpm <= 0)
        {
            _history.Clear();
            if (_reported == 0) return;
            _reported = 0;
            BpmChanged?.Invoke(0);
            return;
        }

        // Mediane glissante : une estimation isolee aberrante ne doit pas
        // faire varier la vitesse du disque.
        _history.Add(bpm);
        if (_history.Count > 7) _history.RemoveAt(0);
        if (_history.Count < 3) return;

        var sorted = new List<double>(_history);
        sorted.Sort();
        var median = sorted[sorted.Count / 2];

        // Hysteresis : en deca, la difference est invisible a l'oeil et ne
        // justifie pas de reconstruire l'animation.
        if (Math.Abs(median - _reported) < 1.5) return;

        _reported = median;
        BpmChanged?.Invoke(median);
    }
}
