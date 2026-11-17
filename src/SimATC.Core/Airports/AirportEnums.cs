namespace SimATC.Core.Airports;

// Valores numéricos idénticos a los de la API de facilities del SDK de MSFS 2024
// (SimConnect_AddToFacilityDefinition). No cambiar los números.

public enum RunwayDesignator
{
    None = 0, Left = 1, Right = 2, Center = 3, Water = 4, A = 5, B = 6,
}

public enum FrequencyType
{
    None = 0, Atis = 1, Multicom = 2, Unicom = 3, Ctaf = 4, Ground = 5, Tower = 6, Clearance = 7,
    Approach = 8, Departure = 9, Center = 10, Fss = 11, Awos = 12, Asos = 13, ClearancePreTaxi = 14,
    RemoteClearanceDelivery = 15,
}

public enum TaxiPointType
{
    None = 0, Normal = 1, HoldShort = 2, IlsHoldShort = 4, HoldShortNoDraw = 5, IlsHoldShortNoDraw = 6,
}

public enum HoldShortOrientation
{
    Forward = 0, Reverse = 1,
}

public enum TaxiPathType
{
    None = 0, Taxi = 1, Runway = 2, Parking = 3, Path = 4, Closed = 5, Vehicle = 6, Road = 7, PaintedLine = 8,
}

public enum ParkingType
{
    None = 0, RampGa = 1, RampGaSmall = 2, RampGaMedium = 3, RampGaLarge = 4, RampCargo = 5, RampMilCargo = 6,
    RampMilCombat = 7, GateSmall = 8, GateMedium = 9, GateHeavy = 10, DockGa = 11, Fuel = 12, Vehicle = 13,
    RampGaExtra = 14, GateExtra = 15,
}

/// <summary>Nombre (o sufijo) de un puesto. 12-37 = GATE_A a GATE_Z.</summary>
public enum ParkingName
{
    None = 0, Parking = 1, NParking = 2, NeParking = 3, EParking = 4, SeParking = 5, SParking = 6, SwParking = 7,
    WParking = 8, NwParking = 9, Gate = 10, Dock = 11,
    GateA = 12, GateB, GateC, GateD, GateE, GateF, GateG, GateH, GateI, GateJ, GateK, GateL, GateM, GateN, GateO,
    GateP, GateQ, GateR, GateS, GateT, GateU, GateV, GateW, GateX, GateY, GateZ,
}

public enum ApproachType
{
    Undefined = 0, Gps = 1, Vor = 2, Ndb = 3, Ils = 4, Localizer = 5, Sdf = 6, Lda = 7, VorDme = 8, NdbDme = 9,
    Rnav = 10, LocalizerBackCourse = 11,
}

/// <summary>Tipo de tramo de procedimiento (ARINC 424).</summary>
public enum LegType
{
    Unknown = 0, AF = 1, CA = 2, CD = 3, CF = 4, CI = 5, CR = 6, DF = 7, FA = 8, FC = 9, FD = 10, FM = 11,
    HA = 12, HF = 13, HM = 14, IF = 15, PI = 16, RF = 17, TF = 18, VA = 19, VD = 20, VI = 21, VM = 22, VR = 23,
}

public enum TurnDirection
{
    None = 0, Left = 1, Right = 2, Either = 3,
}

public enum AltitudeConstraint
{
    None = 0, At = 1, AtOrAbove = 2, AtOrBelow = 3, Between = 4,
}

public enum SpeedConstraint
{
    None = 0, At = 1, AtOrAbove = 2, AtOrBelow = 3,
}
