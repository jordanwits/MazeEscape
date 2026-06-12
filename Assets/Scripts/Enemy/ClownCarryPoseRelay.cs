using UnityEngine;

/// <summary>
/// Publishes which base-layer state the Clown is in (as the CarryPose int parameter) so the
/// "Hammer Carry" upper-body layer can follow it with its own Any State transitions.
///
/// The carry layer cannot be a synced layer: synced layers have a null
/// AnimatorControllerLayer.stateMachine, which NGO's NetworkAnimator NPEs on while building its
/// transition info (NetworkAnimator.BuildTransitionStateInfoList), aborting ServerNetworkAnimator
/// setup at spawn. A regular layer driven by this relay behaves identically and parses cleanly.
///
/// Runs locally on every peer — clients play the same controller, so their own base-layer state
/// changes (replicated by the NetworkAnimator) re-publish the value with no extra network traffic.
/// </summary>
public class ClownCarryPoseRelay : StateMachineBehaviour
{
    [Tooltip("Value written to the int parameter when this base state is entered. 0=Idle, 1=Walk, 2=Run, 3=Hammer Swing.")]
    public int poseIndex;

    [Tooltip("Animator int parameter the carry layer's transitions are conditioned on.")]
    public string parameterName = "CarryPose";

    int _parameterHash;

    public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        if (_parameterHash == 0)
            _parameterHash = Animator.StringToHash(parameterName);

        animator.SetInteger(_parameterHash, poseIndex);
    }
}
