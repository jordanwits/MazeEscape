/// <summary>
/// Implemented by every server-side enemy AI that a <see cref="FlashbangGrenade"/> can blind. The blind
/// STATE itself lives on <see cref="EnemyBlindEffect"/> (added to the enemy at detonation time); this
/// interface is only how the grenade finds the AI in an overlap query and how the AI gets one chance to
/// tear down whatever it was doing — mid-swing, mid-grab, mid-chase — the moment the flash lands.
/// </summary>
public interface IBlindableEnemy
{
    /// <summary>
    /// Server-side, called once when the blind starts (or is refreshed by a second grenade). Implementations
    /// abort the current attack/carry, drop the target, and stop the NavMeshAgent so the blind wander in
    /// <c>Update</c> starts from a clean state.
    /// </summary>
    void OnFlashbangBlinded(float seconds);
}
