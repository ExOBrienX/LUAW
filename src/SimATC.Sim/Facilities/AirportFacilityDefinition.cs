using Microsoft.FlightSimulator.SimConnect;

namespace SimATC.Sim.Facilities;

/// <summary>
/// Definición jerárquica de facilities para un aeropuerto completo (V-04): se abre cada nodo con "OPEN X", se piden
/// sus campos y se cierra con "CLOSE X". Un mismo tipo de nodo usa siempre los mismos campos, porque el struct se
/// registra por tipo.
/// </summary>
internal static class AirportFacilityDefinition
{
    public static void Register(SimConnect sim, Enum definitionId)
    {
        void Add(string s) => sim.AddToFacilityDefinition(definitionId, s);

        void Node(string name, string[] fields, Action? children = null)
        {
            Add("OPEN " + name);
            foreach (var f in fields)
                Add(f);
            children?.Invoke();
            Add("CLOSE " + name);
        }

        void Legs(string name) => Node(name, LegRaw.Fields);

        void ProcedureNode(string name) => Node(name, ProcedureRaw.Fields, () =>
        {
            Node("RUNWAY_TRANSITION", RunwayTransitionRaw.Fields, () => Legs("APPROACH_LEG"));
            Node("ENROUTE_TRANSITION", NamedTransitionRaw.Fields, () => Legs("APPROACH_LEG"));
            Legs("APPROACH_LEG");
        });

        Node("AIRPORT", AirportRaw.Fields, () =>
        {
            Node("RUNWAY", RunwayRaw.Fields);
            Node("FREQUENCY", FrequencyRaw.Fields);
            Node("TAXI_POINT", TaxiPointRaw.Fields);
            Node("TAXI_PARKING", TaxiParkingRaw.Fields, () => Node("AIRLINE", AirlineRaw.Fields));
            Node("TAXI_PATH", TaxiPathRaw.Fields);
            Node("TAXI_NAME", TaxiNameRaw.Fields);
            ProcedureNode("DEPARTURE");
            ProcedureNode("ARRIVAL");
            Node("APPROACH", ApproachRaw.Fields, () =>
            {
                Node("APPROACH_TRANSITION", NamedTransitionRaw.Fields, () => Legs("APPROACH_LEG"));
                Legs("FINAL_APPROACH_LEG");
                Legs("MISSED_APPROACH_LEG");
            });
        });

        sim.RegisterFacilityDataDefineStruct<AirportRaw>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
        sim.RegisterFacilityDataDefineStruct<RunwayRaw>(SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY);
        sim.RegisterFacilityDataDefineStruct<FrequencyRaw>(SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY);
        sim.RegisterFacilityDataDefineStruct<TaxiPointRaw>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_POINT);
        sim.RegisterFacilityDataDefineStruct<TaxiParkingRaw>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING);
        sim.RegisterFacilityDataDefineStruct<AirlineRaw>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING_AIRLINE);
        sim.RegisterFacilityDataDefineStruct<TaxiPathRaw>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PATH);
        sim.RegisterFacilityDataDefineStruct<TaxiNameRaw>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_NAME);
        sim.RegisterFacilityDataDefineStruct<ProcedureRaw>(SIMCONNECT_FACILITY_DATA_TYPE.DEPARTURE);
        sim.RegisterFacilityDataDefineStruct<ProcedureRaw>(SIMCONNECT_FACILITY_DATA_TYPE.ARRIVAL);
        sim.RegisterFacilityDataDefineStruct<RunwayTransitionRaw>(SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY_TRANSITION);
        sim.RegisterFacilityDataDefineStruct<NamedTransitionRaw>(SIMCONNECT_FACILITY_DATA_TYPE.ENROUTE_TRANSITION);
        sim.RegisterFacilityDataDefineStruct<ApproachRaw>(SIMCONNECT_FACILITY_DATA_TYPE.APPROACH);
        sim.RegisterFacilityDataDefineStruct<NamedTransitionRaw>(SIMCONNECT_FACILITY_DATA_TYPE.APPROACH_TRANSITION);
        sim.RegisterFacilityDataDefineStruct<LegRaw>(SIMCONNECT_FACILITY_DATA_TYPE.APPROACH_LEG);
        sim.RegisterFacilityDataDefineStruct<LegRaw>(SIMCONNECT_FACILITY_DATA_TYPE.FINAL_APPROACH_LEG);
        sim.RegisterFacilityDataDefineStruct<LegRaw>(SIMCONNECT_FACILITY_DATA_TYPE.MISSED_APPROACH_LEG);
    }
}
