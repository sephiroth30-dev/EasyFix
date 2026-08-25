using EasyFix.Core.Classification;
using EasyFix.Core.Configuration;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests del clasificador de 3 capas. Es el mecanismo que decide qué se toca en el equipo de un
/// cliente, así que los casos que importan son los adversarios: nombres que mienten, firmas que no
/// validan, y el default cuando nada matchea.
/// </summary>
public sealed class StartupClassifierTests
{
    private const string SpotifyPublisher = "Spotify AB";
    private const string MicrosoftPublisher = "Microsoft Windows";

    private static StartupClassifier BuildClassifier() => new(new ClassifierOptions
    {
        HardBlock = new HardBlockOptions
        {
            ProtectedPathPrefixes = new[] { @"C:\Windows\System32", @"C:\Windows\SysWOW64" },
            ProtectedCertificatePublishers = new[] { MicrosoftPublisher, "Pulse Secure, LLC", "NVIDIA Corporation" },
            BlockIfHasRunningDependents = true,
            BlockIfDriverAssociated = true,
        },
        AutoDisableAllowlist = new[]
        {
            new AllowlistEntry { Publisher = SpotifyPublisher, Product = "Spotify", Loses = "Se abre a mano" },
            new AllowlistEntry
            {
                Publisher = "Microsoft Corporation",
                Product = "Microsoft Teams",
                Loses = "Solo la versión personal",
                OnlyIfNotDomainJoined = true,
            },
        },
    });

    private static readonly SystemContext HomePc = new(IsDomainJoined: false, IsSsd: true, TotalRamGb: 16);
    private static readonly SystemContext DomainPc = new(IsDomainJoined: true, IsSsd: true, TotalRamGb: 16);

    private static StartupCandidate Candidate(
        string displayName,
        string? publisher,
        bool signatureValid = true,
        string? path = @"C:\Program Files\App\app.exe",
        string? product = null,
        bool securityProduct = false,
        bool driver = false,
        string[]? dependents = null) =>
        new(
            Id: $"HKCU\\...\\Run\\{displayName}",
            DisplayName: displayName,
            ExecutablePath: path,
            ProductName: product,
            Signature: new SignatureInfo(signatureValid, publisher),
            Location: StartupLocation.RegistryRunCurrentUser,
            RunningDependents: dependents,
            HasAssociatedDriver: driver,
            IsRegisteredSecurityProduct: securityProduct);

    // ---- Capa 1: bloqueo duro --------------------------------------------------------------

    [Fact]
    public void ProductoDeSeguridadRegistrado_QuedaBloqueado()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Windows Defender", "Microsoft Corporation", securityProduct: true),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
        Assert.False(result.CanDisableAutomatically);
    }

    [Fact]
    public void ProductoDeSeguridad_GanaSobreLaListaBlanca()
    {
        // Un producto que está en la lista blanca Y registrado como seguridad tiene que quedar
        // bloqueado: la Capa 1 se evalúa primero y gana.
        Classification result = BuildClassifier().Classify(
            Candidate("Spotify", SpotifyPublisher, securityProduct: true),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
    }

    [Fact]
    public void PublisherProtegido_QuedaBloqueado()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Panel de control NVIDIA", "NVIDIA Corporation"),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
    }

    [Fact]
    public void EjecutableEnSystem32_QuedaBloqueado()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Algo", "Editor Cualquiera", path: @"C:\Windows\System32\algo.exe"),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
    }

    [Fact]
    public void RutaQueSoloComparteElPrefijoDeTexto_NoQuedaBloqueada()
    {
        // C:\Windows\System32Evil NO está dentro de C:\Windows\System32. Un StartsWith pelado
        // daría un falso positivo acá — y en el sentido contrario, protegería a un malware.
        Classification result = BuildClassifier().Classify(
            Candidate("Sospechoso", "Editor Cualquiera", path: @"C:\Windows\System32Evil\x.exe"),
            HomePc);

        Assert.NotEqual(ClassificationTier.HardBlocked, result.Tier);
    }

    [Fact]
    public void ServicioConDependientesEnEjecucion_QuedaBloqueado()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("RpcSs", "Editor Cualquiera", dependents: new[] { "DcomLaunch", "LSM" }),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
        Assert.Contains("DcomLaunch", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConDriverAsociado_QuedaBloqueado()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Servicio de audio", "Editor Cualquiera", driver: true),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
    }

    // ---- El caso que motiva todo el diseño -------------------------------------------------

    [Fact]
    public void MalwareQueSeLlamaComoUnProgramaProtegido_NoQuedaBloqueado()
    {
        // Un archivo llamado "MyVPN" sin firma válida. Con match por nombre (*VPN*) quedaría
        // protegido; con verificación por certificado cae en Capa 3 y se marca como posible malware.
        Classification result = BuildClassifier().Classify(
            Candidate("MyVPN Service", publisher: null, signatureValid: false, path: @"C:\Users\bob\AppData\Roaming\myvpn.exe"),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
        Assert.True(result.PossibleMalware);
    }

    [Fact]
    public void PublisherProtegidoDeclaradoPeroSinFirmaValida_NoOtorgaProteccion()
    {
        // Cualquiera puede escribir "Microsoft Windows" en los metadatos de un binario sin firmar.
        // Solo un certificado que valida cuenta como evidencia de identidad.
        Classification result = BuildClassifier().Classify(
            Candidate("svchost", MicrosoftPublisher, signatureValid: false, path: @"C:\Users\bob\svchost.exe"),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
        Assert.True(result.PossibleMalware);
    }

    [Fact]
    public void VpnRealQueNoContieneVpnEnElNombre_QuedaBloqueadaPorSuCertificado()
    {
        // El caso inverso: "Pulse Secure" no matchea el patrón *VPN* pero sí su certificado.
        Classification result = BuildClassifier().Classify(
            Candidate("Pulse Secure Service", "Pulse Secure, LLC"),
            HomePc);

        Assert.Equal(ClassificationTier.HardBlocked, result.Tier);
    }

    // ---- Capa 2: lista blanca --------------------------------------------------------------

    [Fact]
    public void EnListaBlancaConFirmaValida_SeDesactivaSolo()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Spotify", SpotifyPublisher, product: "Spotify"),
            HomePc);

        Assert.Equal(ClassificationTier.AutoSafe, result.Tier);
        Assert.True(result.CanDisableAutomatically);
        Assert.Equal("Se abre a mano", result.WhatYouLose);
    }

    [Fact]
    public void NombreCoincidePeroLaFirmaNoValida_NoLlegaACapa2()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Spotify", SpotifyPublisher, signatureValid: false, product: "Spotify"),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
    }

    [Fact]
    public void ProductoCorrectoPeroOtroPublisher_NoLlegaACapa2()
    {
        // Un ejecutable firmado por otra empresa que se hace llamar "Spotify".
        Classification result = BuildClassifier().Classify(
            Candidate("Spotify", "Fake Software Ltd", product: "Spotify"),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
        Assert.False(result.PossibleMalware); // firmado, solo desconocido
    }

    [Fact]
    public void TeamsPersonal_SeDesactivaSolo_PeroEnDominioNo()
    {
        StartupClassifier classifier = BuildClassifier();
        StartupCandidate teams = Candidate("Microsoft Teams", "Microsoft Corporation", product: "Microsoft Teams");

        Assert.Equal(ClassificationTier.AutoSafe, classifier.Classify(teams, HomePc).Tier);
        Assert.Equal(ClassificationTier.NeedsApproval, classifier.Classify(teams, DomainPc).Tier);
    }

    [Fact]
    public void PublisherComparaSinDistinguirMayusculas()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Spotify", "spotify ab", product: "Spotify"),
            HomePc);

        Assert.Equal(ClassificationTier.AutoSafe, result.Tier);
    }

    // ---- Capa 3: el default ----------------------------------------------------------------

    [Fact]
    public void DesconocidoConFirmaValida_PideAprobacion()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Agente de gestión de la empresa", "Some Corp S.A."),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
        Assert.False(result.PossibleMalware);
        Assert.False(result.CanDisableAutomatically);
    }

    [Fact]
    public void SinRutaDeEjecutable_NoRevienta_YPideAprobacion()
    {
        Classification result = BuildClassifier().Classify(
            Candidate("Entrada huérfana", "Some Corp S.A.", path: null),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
    }

    [Fact]
    public void ClassifyAll_ConservaElOrdenDeEntrada()
    {
        StartupCandidate[] input =
        {
            Candidate("Spotify", SpotifyPublisher, product: "Spotify"),
            Candidate("Defender", "Microsoft Corporation", securityProduct: true),
            Candidate("Desconocido", "Some Corp S.A."),
        };

        var results = BuildClassifier().ClassifyAll(input, HomePc);

        Assert.Equal(3, results.Count);
        Assert.Equal(ClassificationTier.AutoSafe, results[0].Classification.Tier);
        Assert.Equal(ClassificationTier.HardBlocked, results[1].Classification.Tier);
        Assert.Equal(ClassificationTier.NeedsApproval, results[2].Classification.Tier);
    }

    [Fact]
    public void ListaBlancaVacia_HaceQueTodoPidaAprobacion()
    {
        // Una configuración corrupta o vacía tiene que fallar CERRADO: nada en automático.
        var classifier = new StartupClassifier(new ClassifierOptions());

        Classification result = classifier.Classify(
            Candidate("Spotify", SpotifyPublisher, product: "Spotify"),
            HomePc);

        Assert.Equal(ClassificationTier.NeedsApproval, result.Tier);
    }
}
