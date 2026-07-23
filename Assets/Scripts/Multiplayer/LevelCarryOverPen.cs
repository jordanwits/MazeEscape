using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// DontDestroyOnLoad parking spot for the hotbar items that ride along to the next maze section (see
/// <see cref="LevelCarryOverStore"/>). Items are re-parented here for the duration of the load and pull
/// themselves back out when the restored inventory re-seats them on the new avatar.
///
/// The pen sits far below any level geometry: a parked item is still registered under its item id, and
/// <see cref="GrabbableInventoryItem"/>'s nearest-match fallbacks are distance-gated, so parking it out of
/// range is what keeps a carried item from being mistaken for a world pickup during the transition.
/// </summary>
[DisallowMultipleComponent]
public class LevelCarryOverPen : MonoBehaviour
{
    const string RootName = "LevelCarryOverPen";
    static readonly Vector3 ParkPosition = new Vector3(0f, -5000f, 0f);

    /// <summary>
    /// Grace period after the next section finishes loading before unclaimed items are destroyed. Generous:
    /// the server can spend several seconds waiting for a valid level-start spawn before it re-spawns the
    /// avatars that claim these items back.
    /// </summary>
    const float SweepDelaySeconds = 30f;

    static LevelCarryOverPen s_instance;
    Coroutine _sweepRoutine;

    public static Transform EnsureRoot()
    {
        if (s_instance != null)
            return s_instance.transform;

        GameObject root = new GameObject(RootName);
        root.transform.position = ParkPosition;
        DontDestroyOnLoad(root);
        s_instance = root.AddComponent<LevelCarryOverPen>();
        return s_instance.transform;
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    void OnDestroy()
    {
        if (s_instance == this)
            s_instance = null;
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!MultiplayerSceneFlow.IsMazeGameplayScene(scene.name))
        {
            // Back in the menu (session ended, host stopped, disconnect): the run is over, so nothing is
            // owed to anyone. Drop the snapshots and the parked items together.
            LevelCarryOverStore.ClearAll();
            Destroy(gameObject);
            return;
        }

        if (_sweepRoutine != null)
            StopCoroutine(_sweepRoutine);

        _sweepRoutine = StartCoroutine(SweepUnclaimedAfterDelay());
    }

    /// <summary>
    /// Anything still parented here once the section is fully underway belongs to nobody — e.g. a player who
    /// disconnected during the transition. Destroy it rather than leave a registered ghost item alive.
    /// </summary>
    IEnumerator SweepUnclaimedAfterDelay()
    {
        yield return new WaitForSeconds(SweepDelaySeconds);
        _sweepRoutine = null;

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child != null)
                Destroy(child.gameObject);
        }
    }
}
