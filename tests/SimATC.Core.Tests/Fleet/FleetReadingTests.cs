using SimATC.Core.Fleet;

namespace SimATC.Core.Tests.Fleet;

public sealed class FleetReadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SimATC.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // TEST DATA: fragmentos con la forma de los aircraft.cfg de FSLTL; nombres y valores inventados.
    private const string BaseCfg = """
        [VERSION]
        [GENERAL]
        icao_type_designator = "A20N"
        icao_WTC = "M"
        icao_engine_count = 2
        [FLTSIM.0]
        title = "TEST_A20N_ZZZZ" ; Variation name
        atc_parking_types = "GATE,RAMP"
        icao_airline = "ZZZZ" ;
        [FLTSIM.1]
        title = "TEST_A20N_XYZ_Test-Air"
        atc_parking_codes = "XYZ"
        atc_parking_types = "GATE"
        icao_airline = "XYZ"
        """;

    private const string FlightModel = """
        [AIRPLANE_GEOMETRY]
        wing_span = 104 ; (feet)
        [CONTACT_POINTS]
        point.0 = 1,  37.32,   0.00,  -11.69, 1600, 0, 1.263 ;
        point.1 = 1, -4.16, -12.65, -12.47, 1600, 1, 2.051 ;
        point.2 = 1, -4.16,  12.65, -12.47, 1600, 2, 2.051 ;
        point.3 = 2,  60.00,   0.00,   0.00 ; no es rueda
        [WEIGHT_AND_BALANCE]
        reference_datum_position = 0, 0, 0
        """;

    private const string LiveryCfg = """
        [VARIATION]
        base_container = "..\TEST_A20N"
        [FLTSIM.0]
        title = "TEST_A20N_QQQ_Other ; with semicolon"
        icao_airline = "qqq"
        """;

    [Fact]
    public void CfgFile_ParsesSectionsKeysQuotesAndComments()
    {
        var cfg = CfgFile.Parse(BaseCfg);

        Assert.Equal("A20N", cfg.Get("general", "ICAO_TYPE_DESIGNATOR"));
        Assert.Equal("2", cfg.Get("GENERAL", "icao_engine_count"));
        Assert.Equal("TEST_A20N_ZZZZ", cfg.Get("FLTSIM.0", "title"));
        Assert.Null(cfg.Get("FLTSIM.0", "nope"));
        Assert.Equal("TEST_A20N_QQQ_Other ; with semicolon", CfgFile.Parse(LiveryCfg).Get("FLTSIM.0", "title"));
    }

    [Fact]
    public void Scan_ReadsModelsAndResolvesBaseContainer()
    {
        var pkg = Path.Combine(_dir, "fsltl-traffic-test");
        var baseDir = Directory.CreateDirectory(Path.Combine(pkg, "SimObjects", "Airplanes", "TEST_A20N")).FullName;
        File.WriteAllText(Path.Combine(baseDir, "aircraft.cfg"), BaseCfg);
        File.WriteAllText(Path.Combine(baseDir, "flight_model.cfg"), FlightModel);
        var liveryDir = Directory.CreateDirectory(Path.Combine(pkg, "SimObjects", "Airplanes", "TEST_A20N_QQQ")).FullName;
        File.WriteAllText(Path.Combine(liveryDir, "aircraft.cfg"), LiveryCfg);
        Directory.CreateDirectory(Path.Combine(_dir, "otro-paquete")); // no es de tráfico: se ignora

        var packages = FleetScanner.FindTrafficPackages([_dir]);
        var models = FleetScanner.Scan(packages).ToDictionary(m => m.Title);

        Assert.Single(packages);
        Assert.Equal(3, models.Count);
        var generic = models["TEST_A20N_ZZZZ"];
        Assert.True(generic.IsGeneric);
        Assert.Equal(ModelSource.Fsltl, generic.Source);
        Assert.Equal(WakeCategory.Medium, generic.Wake);
        Assert.Equal(31.70, generic.WingspanM, 2); // 104 ft
        Assert.Equal(["GATE", "RAMP"], generic.ParkingTypes);
        Assert.Equal("XYZ", models["TEST_A20N_XYZ_Test-Air"].IcaoAirline);
        var livery = models["TEST_A20N_QQQ_Other ; with semicolon"];
        Assert.Equal("QQQ", livery.IcaoAirline);
        Assert.Equal("A20N", livery.IcaoType);        // del modelo base
        Assert.Equal(31.70, livery.WingspanM, 2);     // del flight_model.cfg del base
        Assert.Equal(11.38, generic.NoseGearM, 2);    // rueda más adelantada sobre el eje: 37,32 ft
        Assert.Equal(11.38, livery.NoseGearM, 2);
    }

    [Fact]
    public void FsltlRules_ParseAirlineGenericAndAlternatives()
    {
        // TEST DATA
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ModelMatchRuleSet>
            <ModelMatchRule TypeCode = "A20N" ModelName ="FSLTL_A20N_ZZZZ" />
            <ModelMatchRule CallsignPrefix="xyz" TypeCode = "a320" ModelName ="M1//M2 with space//M3" />
            <ModelMatchRule TypeCode = "" ModelName ="ignored" />
            </ModelMatchRuleSet>
            """;

        var rules = FsltlRules.Parse(new StringReader(xml));

        Assert.Equal(2, rules.Count);
        Assert.Equal("", rules[0].Airline);
        Assert.Equal(["FSLTL_A20N_ZZZZ"], rules[0].Models);
        Assert.Equal("XYZ", rules[1].Airline);
        Assert.Equal("A320", rules[1].TypeCode);
        Assert.Equal(["M1", "M2 with space", "M3"], rules[1].Models);
    }

    [Fact]
    public void MsfsInstall_ReadsInstalledPackagesPath()
    {
        // TEST DATA
        const string userCfg = "Version 1\nInstalledPackagesPath \"D:\\MSFS\\Packages\"\nOther 1\n";

        Assert.Equal(@"D:\MSFS\Packages", MsfsInstall.ReadInstalledPackagesPath(userCfg));
        Assert.Null(MsfsInstall.ReadInstalledPackagesPath("Nada"));
    }
}
