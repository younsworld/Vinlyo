using System.Runtime.InteropServices;

namespace Vinlyo;

/// <summary>
/// Capture de ce que Windows envoie aux haut-parleurs, via le mode loopback de
/// WASAPI. On n'ecoute pas le microphone : on relit le melange que la carte son
/// est en train de jouer, quelle que soit l'application qui le produit.
///
/// Tout est fait en interop COM directe, sans aucune bibliotheque tierce. La
/// capture vit sur son propre thread et rend des echantillons mono.
///
/// Toute panne est silencieuse et definitive pour la session : si la capture
/// n'aboutit pas, le tempo reste inconnu et le widget retombe simplement sur
/// sa vitesse fixe. Aucune raison de faire echouer le widget entier parce que
/// le peripherique audio est capricieux.
/// </summary>
public sealed class AudioLoopback : IDisposable
{
    /// <summary>Echantillons mono, nombre valide, frequence d'echantillonnage.</summary>
    public event Action<float[], int, int>? SamplesAvailable;

    private Thread? _thread;
    private volatile bool _running;

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;

        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vinlyo.AudioLoopback",
            // Sous la priorite normale : la capture ne doit jamais disputer du
            // temps processeur a l'interface.
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
    }

    public void Dispose() => Stop();

    // --- Boucle de capture -------------------------------------------------

    private void Run()
    {
        try
        {
            Capture();
        }
        catch
        {
            // Peripherique absent, format refuse, COM indisponible : on
            // abandonne la detection de tempo sans bruit.
        }
    }

    private void Capture()
    {
        var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
        if (enumeratorType is null) return;

        var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
        if (enumerator is null) return;

        // eRender + eConsole : le peripherique de sortie par defaut.
        if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device) != 0 || device is null)
            return;

        var audioClientId = IID_IAudioClient;
        if (device.Activate(ref audioClientId, ClsCtxAll, IntPtr.Zero, out var clientObject) != 0)
            return;

        var client = (IAudioClient)clientObject;

        // Le format de melange est impose par Windows : en mode partage on ne
        // negocie pas, on s'y conforme.
        if (client.GetMixFormat(out var formatPointer) != 0 || formatPointer == IntPtr.Zero)
            return;

        try
        {
            var format = Marshal.PtrToStructure<WaveFormatEx>(formatPointer);
            var sampleType = IdentifySampleType(formatPointer, format);
            if (sampleType == SampleType.Unsupported) return;

            var channels = format.Channels;
            var sampleRate = (int)format.SamplesPerSec;
            if (channels == 0 || sampleRate <= 0) return;

            // Le mode evenementiel est refuse par certaines piles audio en
            // loopback. On le tente, et on retombe sur une relecture cadencee
            // si l'initialisation echoue pour ce motif precis.
            var useEvents = true;
            var result = client.Initialize(ShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDurationHns, 0, formatPointer, IntPtr.Zero);

            if (result != 0)
            {
                useEvents = false;
                result = client.Initialize(ShareModeShared, StreamFlagsLoopback,
                    BufferDurationHns, 0, formatPointer, IntPtr.Zero);
            }

            if (result != 0) return;

            using var readyEvent = useEvents ? new AutoResetEvent(false) : null;
            if (useEvents && client.SetEventHandle(readyEvent!.SafeWaitHandle.DangerousGetHandle()) != 0)
                return;

            var captureClientId = IID_IAudioCaptureClient;
            if (client.GetService(ref captureClientId, out var captureObject) != 0) return;
            var capture = (IAudioCaptureClient)captureObject;

            if (client.Start() != 0) return;

            try
            {
                Pump(capture, channels, sampleRate, sampleType, readyEvent);
            }
            finally
            {
                client.Stop();
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPointer);
        }
    }

    private void Pump(IAudioCaptureClient capture, int channels, int sampleRate,
        SampleType sampleType, AutoResetEvent? readyEvent)
    {
        // Assez large pour un paquet WASAPI confortable ; reutilise a chaque
        // tour pour ne rien allouer dans la boucle de capture.
        var mono = new float[16384];

        while (_running)
        {
            // En mode evenementiel, l'attente ne consomme rien. Le delai
            // maximal evite de rester bloque si le flux de rendu se tait, ce
            // qui arrive quand plus aucune application ne joue.
            if (readyEvent is not null) readyEvent.WaitOne(200);
            else Thread.Sleep(10);

            while (_running)
            {
                if (capture.GetNextPacketSize(out var packetFrames) != 0) return;
                if (packetFrames == 0) break;

                if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0)
                    return;

                try
                {
                    if (frames == 0) continue;

                    var count = Math.Min((int)frames, mono.Length);

                    if ((flags & BufferFlagsSilent) != 0)
                    {
                        Array.Clear(mono, 0, count);
                    }
                    else
                    {
                        Downmix(data, count, channels, sampleType, mono);
                    }

                    SamplesAvailable?.Invoke(mono, count, sampleRate);
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }
    }

    /// <summary>
    /// Reduit le paquet entrelace a un seul canal. Le tempo ne depend pas de la
    /// stereo, et travailler en mono divise d'autant le cout de l'analyse.
    /// </summary>
    private static unsafe void Downmix(IntPtr data, int frames, int channels,
        SampleType sampleType, float[] destination)
    {
        if (sampleType == SampleType.Float32)
        {
            var source = (float*)data;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += source[i * channels + c];
                destination[i] = sum / channels;
            }
        }
        else
        {
            var source = (short*)data;
            for (var i = 0; i < frames; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += source[i * channels + c] / 32768f;
                destination[i] = sum / channels;
            }
        }
    }

    private enum SampleType { Unsupported, Float32, Pcm16 }

    /// <summary>
    /// Determine le type d'echantillon. Windows rend presque toujours du
    /// virgule flottante 32 bits, declare sous forme etendue : il faut alors
    /// aller lire le sous-format apres l'en-tete.
    /// </summary>
    private static SampleType IdentifySampleType(IntPtr pointer, WaveFormatEx format)
    {
        const ushort WaveFormatPcm = 1;
        const ushort WaveFormatIeeeFloat = 3;
        const ushort WaveFormatExtensible = 0xFFFE;

        switch (format.FormatTag)
        {
            case WaveFormatIeeeFloat:
                return format.BitsPerSample == 32 ? SampleType.Float32 : SampleType.Unsupported;

            case WaveFormatPcm:
                return format.BitsPerSample == 16 ? SampleType.Pcm16 : SampleType.Unsupported;

            case WaveFormatExtensible:
                // WAVEFORMATEXTENSIBLE : 18 octets d'en-tete, puis 2 octets de
                // bits utiles, 4 octets de masque de canaux, puis le GUID.
                if (format.ExtraSize < 22) return SampleType.Unsupported;
                var subFormat = Marshal.PtrToStructure<Guid>(pointer + 24);

                if (subFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                    return format.BitsPerSample == 32 ? SampleType.Float32 : SampleType.Unsupported;
                if (subFormat == KSDATAFORMAT_SUBTYPE_PCM)
                    return format.BitsPerSample == 16 ? SampleType.Pcm16 : SampleType.Unsupported;

                return SampleType.Unsupported;

            default:
                return SampleType.Unsupported;
        }
    }

    // --- Declarations COM --------------------------------------------------

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");

    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const uint ClsCtxAll = 23;

    private const int ShareModeShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x2;

    /// <summary>Tampon de 200 ms, exprime en unites de 100 nanosecondes.</summary>
    private const long BufferDurationHns = 2_000_000;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr sessionId);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong counterPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
