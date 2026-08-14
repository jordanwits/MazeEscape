using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Breaks a fraction of the Severance ceiling fixtures: some flicker like a failing tube, some are
/// dead altogether. One of these per level scene (alongside <see cref="MazeLightCuller"/>); it finds
/// fixtures on an interval, so it works with a maze that assembles itself at runtime.
///
/// **Which fixtures fail is decided by hashing their world position, not by <c>Random</c>.** The maze
/// is never network-spawned — every peer builds the same layout locally from a shared seed — so the
/// same fixture ends up at the same world position on every machine, and hashing that position gets
/// all peers to agree on which lights are broken for free, with nothing sent over the wire. Rolling
/// dice per peer instead would leave one player standing in a dead corridor that is lit for everyone
/// else. The position is quantised before hashing so float drift can't flip a fixture's fate.
///
/// The flicker itself is <see cref="RopeLightFlicker"/>, added at runtime to the chosen fixtures — it
/// already does the irregular "bad connection" dropouts and stutter bursts, it gathers the fixture's
/// lights automatically, and <see cref="SeveranceCeilingLight"/> mirrors the result onto the emissive
/// panel so the glowing rectangle dies with the tube. Flicker *timing* is per-peer and unsynchronised,
/// which is fine — it is cosmetic. Only the choice of which fixtures are faulty needs to agree.
/// </summary>
[DisallowMultipleComponent]
public class SeveranceLightFaults : MonoBehaviour
{
    [Header("Fractions (of ALL fixtures, not of each other)")]
    [Tooltip("Share of fixtures that are completely dead — light off, panel dark. Default 0.05 = 5%.")]
    [SerializeField, Range(0f, 1f)] float brokenFraction = 0.05f;

    [Tooltip("Share of fixtures that flicker. Default 0.20 = 20%. With the 5% dead that is a quarter of " +
             "the level's lights faulty in some way; the rest burn steadily.")]
    [SerializeField, Range(0f, 1f)] float flickeringFraction = 0.20f;

    [Tooltip("Change this to reshuffle which fixtures are faulty without moving any geometry. Every peer " +
             "must use the same value, so treat it as level authoring, not a runtime dial.")]
    [SerializeField] int seedSalt = 1;

    [Header("Buzz")]
    [Tooltip("Looping electrical buzz for flickering fixtures. RopeLightFlicker mutes it in lock-step with " +
             "the dropouts, so the sound stutters with the tube. Leave empty for silent flickering.")]
    [SerializeField] AudioClip buzzClip;
    [SerializeField, Range(0f, 1f)] float buzzVolume = 0.175f;
    [Tooltip("Metres within which the buzz plays at full volume before it fades.")]
    [SerializeField, Min(0.01f)] float buzzMinDistance = 2f;
    [Tooltip("Metres at which the buzz fades to silence. Roughly a cell or two so a corridor of faulty " +
             "fixtures doesn't turn into a wall of noise.")]
    [SerializeField, Min(0.1f)] float buzzMaxDistance = 10f;

    [Header("Sparks")]
    [Tooltip("Shower sparks out of a fixture each time its tube cuts out.")]
    [SerializeField] bool enableSparks = true;
    [Tooltip("Additive material for the spark streaks.")]
    [SerializeField] Material sparkMaterial;

    [Header("Discovery")]
    [Tooltip("Seconds between scans that pick up newly spawned fixtures as the maze builds.")]
    [SerializeField, Min(0.25f)] float rescanInterval = 2f;

    readonly HashSet<SeveranceCeilingLight> _processed = new HashSet<SeveranceCeilingLight>();
    readonly List<SeveranceCeilingLight> _pruneScratch = new List<SeveranceCeilingLight>();

    float _nextScan;
    int _brokenCount;
    int _flickerCount;
    int _totalCount;

    /// <summary>Fixtures seen so far, and how many of them were made faulty. For diagnostics.</summary>
    public void GetCounts(out int total, out int broken, out int flickering)
    {
        total = _totalCount; broken = _brokenCount; flickering = _flickerCount;
    }

    void OnEnable()
    {
        _nextScan = 0f;
    }

    void Update()
    {
        if (Time.unscaledTime < _nextScan)
            return;
        _nextScan = Time.unscaledTime + Mathf.Max(0.25f, rescanInterval);
        Scan();
    }

    void Scan()
    {
        Prune();

        // Active objects only: a fixture whose GameObject has not been enabled yet has not run Awake,
        // so it has no light list to drive. It gets picked up on a later pass.
        SeveranceCeilingLight[] all = Object.FindObjectsByType<SeveranceCeilingLight>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            SeveranceCeilingLight fixture = all[i];
            if (fixture == null || !_processed.Add(fixture))
                continue;

            _totalCount++;
            float roll = Hash01(fixture.transform.position, seedSalt);

            if (roll < brokenFraction)
            {
                fixture.SetOutput(0f);
                _brokenCount++;
            }
            else if (roll < brokenFraction + flickeringFraction)
            {
                MakeFlickering(fixture);
                _flickerCount++;
            }
        }
    }

    void MakeFlickering(SeveranceCeilingLight fixture)
    {
        RopeLightFlicker flicker = fixture.GetComponent<RopeLightFlicker>();
        if (flicker == null)
            flicker = fixture.gameObject.AddComponent<RopeLightFlicker>();

        // AddComponent has already run the flicker's Awake (and its audio setup, with no clip), so the
        // clip has to be handed over afterwards rather than assigned up front.
        if (buzzClip != null)
            flicker.SetFlickerAudio(buzzClip, buzzVolume, buzzMinDistance, buzzMaxDistance);

        if (!enableSparks)
            return;

        SeveranceLightSparks sparks = fixture.GetComponent<SeveranceLightSparks>();
        if (sparks == null)
            sparks = fixture.gameObject.AddComponent<SeveranceLightSparks>();
        sparks.SetSparkMaterial(sparkMaterial);
    }

    void Prune()
    {
        if (_processed.Count == 0)
            return;

        _pruneScratch.Clear();
        foreach (SeveranceCeilingLight fixture in _processed)
        {
            if (fixture == null)
                _pruneScratch.Add(fixture);
        }
        for (int i = 0; i < _pruneScratch.Count; i++)
            _processed.Remove(_pruneScratch[i]);
        _pruneScratch.Clear();
    }

    /// <summary>
    /// Stable 0-1 value for a world position. Quantised to 10cm first: the intent is that every peer
    /// hashes the identical integers, and a fixture sitting a hair off between machines must not land
    /// on the other side of a threshold.
    /// </summary>
    static float Hash01(Vector3 position, int salt)
    {
        unchecked
        {
            int x = Mathf.RoundToInt(position.x * 10f);
            int y = Mathf.RoundToInt(position.y * 10f);
            int z = Mathf.RoundToInt(position.z * 10f);

            uint h = (uint)salt * 2654435761u;
            h ^= (uint)x * 2246822519u; h = (h << 13) | (h >> 19);
            h ^= (uint)y * 3266489917u; h = (h << 17) | (h >> 15);
            h ^= (uint)z * 668265263u;  h = (h << 5)  | (h >> 27);

            // Final avalanche so neighbouring cells don't come out correlated — fixtures sit on a
            // regular 6m grid, and a weak mix would put all the dead ones in stripes.
            h ^= h >> 15; h *= 2246822519u;
            h ^= h >> 13; h *= 3266489917u;
            h ^= h >> 16;

            return (h & 0xFFFFFFu) / (float)0x1000000;
        }
    }
}
