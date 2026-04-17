using System.Collections.Generic;
using UnityEngine;

public readonly struct MazePieceMatch
{
    public MazePieceMatch(GameObject prefab, MazePieceDefinition definition, Quaternion rotation, MazeFaceMask finalOpenFaces, bool useClosedFaceCaps)
    {
        Prefab = prefab;
        Definition = definition;
        Rotation = rotation;
        FinalOpenFaces = finalOpenFaces;
        UseClosedFaceCaps = useClosedFaceCaps;
    }

    public GameObject Prefab { get; }
    public MazePieceDefinition Definition { get; }
    public Quaternion Rotation { get; }
    public MazeFaceMask FinalOpenFaces { get; }
    public bool UseClosedFaceCaps { get; }
}

public static class MazePieceResolver
{
    struct Candidate
    {
        public Candidate(GameObject prefab, MazePieceDefinition definition, int quarterTurns)
        {
            Prefab = prefab;
            Definition = definition;
            QuarterTurns = quarterTurns;
        }

        public GameObject Prefab { get; }
        public MazePieceDefinition Definition { get; }
        public int QuarterTurns { get; }
    }

    public static bool TryResolve(
        ProceduralMazeConfig config,
        MazeFaceMask requiredOpenFaces,
        bool isStart,
        bool isExit,
        int seed,
        Vector2Int cellCoordinates,
        out MazePieceMatch match,
        out string failureReason)
    {
        List<Candidate> candidates = new();

        if (isStart && TryResolveForcedStartPiece(config, requiredOpenFaces, out match))
        {
            failureReason = null;
            return true;
        }

        if ((isStart || isExit) && AddCandidates(config.EnumerateSpecialPrefabs(), requiredOpenFaces, isStart, isExit, candidates))
        {
            match = CreateMatch(ChooseCandidate(candidates, seed, cellCoordinates, requiredOpenFaces, 17u), requiredOpenFaces);
            failureReason = null;
            return true;
        }

        MazePieceCategory category = DetermineCategory(requiredOpenFaces);
        candidates.Clear();
        AddCandidates(config.EnumerateTopologyPrefabs(category), requiredOpenFaces, isStart, isExit, candidates, category);

        if (candidates.Count > 0)
        {
            match = CreateMatch(ChooseCandidate(candidates, seed, cellCoordinates, requiredOpenFaces, 53u), requiredOpenFaces);
            failureReason = null;
            return true;
        }

        if ((isStart || isExit) && config.RoomPrefab != null)
        {
            match = new MazePieceMatch(config.RoomPrefab, null, Quaternion.identity, requiredOpenFaces, true);
            failureReason = null;
            return true;
        }

        match = default;
        failureReason = $"No maze piece variant matched faces {requiredOpenFaces} for category {category}.";
        return false;
    }

    /// <summary>
    /// Uses <see cref="ProceduralMazeConfig.ForcedStartPiecePrefab"/> for the start cell when assigned.
    /// Matches when the prefab's openings (after rotation) are a superset of the real cell openings
    /// (equal works for a one-way end-cap). Extras on the prefab get end caps. Pair single-opening art
    /// with <see cref="ProceduralMazeConfig.ForceStartCellSingleOpening"/> so the start is never a two-way corner.
    /// </summary>
    static bool TryResolveForcedStartPiece(ProceduralMazeConfig config, MazeFaceMask requiredOpenFaces, out MazePieceMatch match)
    {
        match = default;
        GameObject prefab = config.ForcedStartPiecePrefab;
        if (prefab == null)
            return false;

        MazePieceDefinition definition = prefab.GetComponent<MazePieceDefinition>();
        if (definition == null || definition.OpenFaces == MazeFaceMask.None)
            return false;

        int turnCount = definition.CanRotate ? 4 : 1;
        for (int quarterTurns = 0; quarterTurns < turnCount; quarterTurns++)
        {
            MazeFaceMask rotated = MazeFaceMaskUtility.Rotate(definition.OpenFaces, quarterTurns);
            if ((requiredOpenFaces & rotated) != requiredOpenFaces)
                continue;

            Quaternion rotation = Quaternion.Euler(0f, quarterTurns * 90f + definition.YawOffset, 0f);
            bool useCaps = definition.UseClosedFaceCaps || rotated != requiredOpenFaces;
            match = new MazePieceMatch(prefab, definition, rotation, requiredOpenFaces, useCaps);
            return true;
        }

        return false;
    }

    public static MazePieceCategory DetermineCategory(MazeFaceMask openFaces)
    {
        int connectionCount = MazeFaceMaskUtility.CountOpenFaces(openFaces);
        return connectionCount switch
        {
            1 => MazePieceCategory.DeadEnd,
            2 when MazeFaceMaskUtility.IsStraight(openFaces) => MazePieceCategory.Straight,
            2 => MazePieceCategory.Corner,
            3 => MazePieceCategory.Tee,
            4 => MazePieceCategory.Cross,
            _ => MazePieceCategory.Special
        };
    }

    static MazePieceMatch CreateMatch(Candidate candidate, MazeFaceMask requiredOpenFaces)
    {
        Quaternion rotation = Quaternion.Euler(0f, candidate.QuarterTurns * 90f + candidate.Definition.YawOffset, 0f);
        return new MazePieceMatch(candidate.Prefab, candidate.Definition, rotation, requiredOpenFaces, candidate.Definition.UseClosedFaceCaps);
    }

    static bool AddCandidates(
        IEnumerable<GameObject> prefabs,
        MazeFaceMask requiredOpenFaces,
        bool isStart,
        bool isExit,
        List<Candidate> candidates,
        MazePieceCategory? expectedCategory = null)
    {
        int initialCount = candidates.Count;
        foreach (GameObject prefab in prefabs)
        {
            if (prefab == null)
                continue;

            MazePieceDefinition definition = prefab.GetComponent<MazePieceDefinition>();
            if (definition == null || !definition.AllowsContext(isStart, isExit))
                continue;

            if (expectedCategory.HasValue && definition.Category != expectedCategory.Value)
                continue;

            if (!definition.TryMatch(requiredOpenFaces, out int quarterTurns))
                continue;

            candidates.Add(new Candidate(prefab, definition, quarterTurns));
        }

        return candidates.Count > initialCount;
    }

    static Candidate ChooseCandidate(List<Candidate> candidates, int seed, Vector2Int cellCoordinates, MazeFaceMask faces, uint salt)
    {
        if (candidates.Count == 1)
            return candidates[0];

        int totalWeight = 0;
        for (int i = 0; i < candidates.Count; i++)
            totalWeight += candidates[i].Definition.Weight;

        uint hash = 2166136261u;
        hash = Mix(hash, unchecked((uint)seed));
        hash = Mix(hash, unchecked((uint)cellCoordinates.x));
        hash = Mix(hash, unchecked((uint)cellCoordinates.y));
        hash = Mix(hash, unchecked((uint)faces));
        hash = Mix(hash, salt);

        int roll = (int)(hash % (uint)Mathf.Max(1, totalWeight));
        for (int i = 0; i < candidates.Count; i++)
        {
            roll -= candidates[i].Definition.Weight;
            if (roll < 0)
                return candidates[i];
        }

        return candidates[candidates.Count - 1];
    }

    static uint Mix(uint hash, uint value)
    {
        unchecked
        {
            hash ^= value + 0x9e3779b9u + (hash << 6) + (hash >> 2);
            return hash;
        }
    }
}
