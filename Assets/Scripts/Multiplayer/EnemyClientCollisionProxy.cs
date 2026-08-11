using UnityEngine;

/// <summary>
/// Enemy avatars disable their <see cref="CharacterController"/> on non-server peers (only the server
/// simulates AI movement) and that CC is the enemy's <b>only</b> collider. The side effect was that on
/// clients, remote players and client-simulated thrown objects passed straight through enemies, while the
/// host — whose CC stays enabled — was solidly blocked. A client-thrown basketball flew through the Clown
/// while a host-thrown one bounced off, visible to everyone since the thrower's sim is authoritative.
/// <para>
/// This attaches a lightweight kinematic capsule mirroring the CharacterController on observer clients so
/// the enemy blocks the same way the host does. We deliberately do <b>not</b> re-enable the CC: it is
/// teleported every frame by the server-authoritative <see cref="Unity.Netcode.Components.NetworkTransform"/>,
/// and an enabled CharacterController fights external transform writes. A kinematic Rigidbody makes the
/// proxy a proper moving collider (no moving-static-collider broadphase churn) that also pushes dynamic
/// thrown props. The proxy lives on the enemy's own layer, so it inherits the exact collision-matrix
/// relationships the CC had and is invisible to AI/trap logic (which runs server-only, where the proxy
/// does not exist).
/// </para>
/// </summary>
public static class EnemyClientCollisionProxy
{
    const string ProxyChildName = "ClientCollisionProxy";

    /// <summary>
    /// Call from an enemy avatar's authority-apply step, right after toggling the CharacterController.
    /// <paramref name="simulatingLocally"/> is true on the server/host and offline (CC enabled → no proxy
    /// needed) and false on observer clients (CC disabled → enable the proxy). Idempotent.
    /// </summary>
    public static void Apply(CharacterController characterController, bool simulatingLocally)
    {
        if (characterController == null)
            return;

        Transform root = characterController.transform;
        Transform existing = root.Find(ProxyChildName);

        if (simulatingLocally)
        {
            // Server / offline: the live CharacterController is the collider. Make sure no stale proxy
            // remains to fight it (e.g. if this instance ever transitioned authority).
            if (existing != null)
                existing.gameObject.SetActive(false);
            return;
        }

        GameObject proxy;
        CapsuleCollider capsule;
        if (existing == null)
        {
            proxy = new GameObject(ProxyChildName);
            proxy.transform.SetParent(root, false);

            Rigidbody body = proxy.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            capsule = proxy.AddComponent<CapsuleCollider>();
            capsule.direction = 1; // Y axis, matching CharacterController
        }
        else
        {
            proxy = existing.gameObject;
            capsule = proxy.GetComponent<CapsuleCollider>();
        }

        // Same layer as the enemy root so blocking matches the CC exactly and server-only AI/trap queries
        // never see this client-only collider.
        proxy.layer = characterController.gameObject.layer;

        if (capsule != null)
        {
            capsule.isTrigger = false;
            capsule.center = characterController.center;
            capsule.radius = characterController.radius;
            capsule.height = characterController.height;
        }

        proxy.SetActive(true);
    }

    /// <summary>
    /// Drops the proxy on an observer client. Call when the server has disabled the real
    /// CharacterController for good (a corpse that should stop blocking) — otherwise the body would keep
    /// blocking on clients only, while the host walks through it.
    /// </summary>
    public static void Deactivate(CharacterController characterController)
    {
        if (characterController == null)
            return;

        Transform existing = characterController.transform.Find(ProxyChildName);
        if (existing != null)
            existing.gameObject.SetActive(false);
    }
}
