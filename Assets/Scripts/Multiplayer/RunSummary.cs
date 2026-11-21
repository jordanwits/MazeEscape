using Unity.Netcode;

/// <summary>
/// The numbers on the run-complete card (<see cref="RunCompleteSequence"/>): one row per player who took part, plus
/// each section's clock. Built once on the server by <see cref="RunStatsTracker.ServerBuildSummary"/> when the last
/// exit cab shuts, then sent whole to every peer, which only ever reads it.
/// </summary>
public struct RunSummary : INetworkSerializable
{
    public enum Outcome : byte
    {
        /// <summary>Alive and aboard when the doors shut.</summary>
        Escaped,
        /// <summary>Still connected, but dead in the last section.</summary>
        Died,
        /// <summary>Disconnected before the end.</summary>
        Left,
    }

    public struct Player
    {
        public ulong ClientId;
        public string Name;
        /// <summary>Index into <see cref="MenuTheme.PlayerColors"/>.</summary>
        public int ColorIndex;
        public Outcome Outcome;
        public float DamageTaken;
        /// <summary>HP restored on teammates with bandages.</summary>
        public float HealingGiven;
        public int Deaths;
        public int Kills;
        /// <summary>Separate chases: an enemy locking on after a few seconds with nothing on this player.</summary>
        public int Chases;
        public float DistanceMeters;
        public int Tickets;
    }

    public Player[] Players;
    public string[] SectionScenes;
    public float[] SectionSeconds;

    public float TotalSeconds
    {
        get
        {
            float total = 0f;
            if (SectionSeconds != null)
            {
                for (int i = 0; i < SectionSeconds.Length; i++)
                    total += SectionSeconds[i];
            }

            return total;
        }
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        int playerCount = Players?.Length ?? 0;
        serializer.SerializeValue(ref playerCount);
        if (serializer.IsReader)
            Players = new Player[playerCount];

        for (int i = 0; i < playerCount; i++)
        {
            ref Player p = ref Players[i];
            string name = p.Name ?? string.Empty;
            byte outcome = (byte)p.Outcome;
            serializer.SerializeValue(ref p.ClientId);
            serializer.SerializeValue(ref name);
            serializer.SerializeValue(ref p.ColorIndex);
            serializer.SerializeValue(ref outcome);
            serializer.SerializeValue(ref p.DamageTaken);
            serializer.SerializeValue(ref p.HealingGiven);
            serializer.SerializeValue(ref p.Deaths);
            serializer.SerializeValue(ref p.Kills);
            serializer.SerializeValue(ref p.Chases);
            serializer.SerializeValue(ref p.DistanceMeters);
            serializer.SerializeValue(ref p.Tickets);
            p.Name = name;
            p.Outcome = (Outcome)outcome;
        }

        int sectionCount = SectionScenes?.Length ?? 0;
        serializer.SerializeValue(ref sectionCount);
        if (serializer.IsReader)
        {
            SectionScenes = new string[sectionCount];
            SectionSeconds = new float[sectionCount];
        }

        for (int i = 0; i < sectionCount; i++)
        {
            string scene = SectionScenes[i] ?? string.Empty;
            float seconds = SectionSeconds != null && i < SectionSeconds.Length ? SectionSeconds[i] : 0f;
            serializer.SerializeValue(ref scene);
            serializer.SerializeValue(ref seconds);
            if (serializer.IsReader)
            {
                SectionScenes[i] = scene;
                SectionSeconds[i] = seconds;
            }
        }
    }
}
